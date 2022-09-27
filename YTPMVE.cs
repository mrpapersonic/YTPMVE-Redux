// based off of YTPMVE by Cantersoft
// mostly rewritten by Paper

using System;
using System.IO;
using System.Collections.Generic;
using System.Windows.Forms;
#if VER_GEQ_14 || VER_GEQ_16
using ScriptPortal.Vegas;
#else
using Sony.Vegas;
#endif
using System.Diagnostics;

#region MidiFile.cs
namespace MidiParser
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    public class MidiFile
    {
        public readonly int Format;

        public readonly int TicksPerQuarterNote;

        public readonly MidiTrack[] Tracks;

        public readonly int TracksCount;

        public MidiFile(Stream stream)
            : this(Reader.ReadAllBytesFromStream(stream))
        {
        }

        public MidiFile(string path)
            : this(File.ReadAllBytes(path))
        {
        }

        public MidiFile(byte[] data)
        {
            int position = 0;

            if (Reader.ReadString(data, ref position, 4) != "MThd")
            {
                throw new FormatException("Invalid file header (expected MThd)");
            }

            if (Reader.Read32(data, ref position) != 6)
            {
                throw new FormatException("Invalid header length (expected 6)");
            }

            this.Format = Reader.Read16(data, ref position);
            this.TracksCount = Reader.Read16(data, ref position);
            this.TicksPerQuarterNote = Reader.Read16(data, ref position);

            if ((this.TicksPerQuarterNote & 0x8000) != 0)
            {
                throw new FormatException("Invalid timing mode (SMPTE timecode not supported)");
            }

            this.Tracks = new MidiTrack[this.TracksCount];

            for (int i = 0; i < this.TracksCount; i++)
            {
                this.Tracks[i] = ParseTrack(i, data, ref position);
            }
        }

        private static bool ParseMetaEvent(
            byte[] data,
            ref int position,
            byte metaEventType,
            ref byte data1,
            ref byte data2)
        {
            switch (metaEventType)
            {
                case (byte)MetaEventType.Tempo:
                    int mspqn = (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
                    data1 = (byte)(60000000.0 / mspqn);
                    position += 4;
                    return true;

                case (byte)MetaEventType.TimeSignature:
                    data1 = data[position + 1];
                    data2 = (byte)Math.Pow(2.0, data[position + 2]);
                    position += 5;
                    return true;

                case (byte)MetaEventType.KeySignature:
                    data1 = data[position + 1];
                    data2 = data[position + 2];
                    position += 3;
                    return true;

                // Ignore Other Meta Events
                default:
                    int length = Reader.ReadVarInt(data, ref position);
                    position += length;
                    return false;
            }
        }

        private static MidiTrack ParseTrack(int index, byte[] data, ref int position)
        {
            if (Reader.ReadString(data, ref position, 4) != "MTrk")
            {
                throw new FormatException("Invalid track header (expected MTrk)");
            }

            int trackLength = Reader.Read32(data, ref position);
            int trackEnd = position + trackLength;

            MidiTrack track = new MidiTrack { Index = index };
            int time = 0;
            byte status = (byte)0;

            while (position < trackEnd)
            {
                time += Reader.ReadVarInt(data, ref position);

                byte peekByte = data[position];

                // If the most significant bit is set then this is a status byte
                if ((peekByte & 0x80) != 0)
                {
                    status = peekByte;
                    ++position;
                }

                // If the most significant nibble is not an 0xF this is a channel event
                if ((status & 0xF0) != 0xF0)
                {
                    // Separate event type from channel into two
                    byte eventType = (byte)(status & 0xF0);
                    byte channel = (byte)((status & 0x0F) + 1);

                    byte data1 = data[position++];

                    // If the event type doesn't start with 0b110 it has two bytes of data (i.e. except 0xC0 and 0xD0)
                    byte data2 = (eventType & 0xE0) != 0xC0 ? data[position++] : (byte)0;

                    // Convert NoteOn events with 0 velocity into NoteOff events
                    if (eventType == (byte)MidiEventType.NoteOn && data2 == 0)
                    {
                        eventType = (byte)MidiEventType.NoteOff;
                    }

                    track.MidiEvents.Add(
                        new MidiEvent { Time = time, Type = eventType, Arg1 = channel, Arg2 = data1, Arg3 = data2 });
                }
                else
                {
                    if (status == 0xFF)
                    {
                        // Meta Event
                        byte metaEventType = Reader.Read8(data, ref position);

                        // There is a group of meta event types reserved for text events which we store separately
                        if (metaEventType >= 0x01 && metaEventType <= 0x0F)
                        {
                            int textLength = Reader.ReadVarInt(data, ref position);
                            string textValue = Reader.ReadString(data, ref position, textLength);
                            TextEvent textEvent = new TextEvent { Time = time, Type = metaEventType, Value = textValue };
                            track.TextEvents.Add(textEvent);
                        }
                        else
                        {
                            byte data1 = (byte)0;
                            byte data2 = (byte)0;

                            // We only handle the few meta events we care about and skip the rest
                            if (ParseMetaEvent(data, ref position, metaEventType, ref data1, ref data2))
                            {
                                track.MidiEvents.Add(
                                    new MidiEvent
                                        {
                                            Time = time,
                                            Type = status,
                                            Arg1 = metaEventType,
                                            Arg2 = data1,
                                            Arg3 = data2
                                        });
                            }
                        }
                    }
                    else if (status == 0xF0 || status == 0xF7)
                    {
                        // SysEx event
                        int length = Reader.ReadVarInt(data, ref position);
                        position += length;
                    }
                    else
                    {
                        ++position;
                    }
                }
            }

            return track;
        }

        private static class Reader
        {
            public static int Read16(byte[] data, ref int i)
            {
                return (data[i++] << 8) | data[i++];
            }

            public static int Read32(byte[] data, ref int i)
            {
                return (data[i++] << 24) | (data[i++] << 16) | (data[i++] << 8) | data[i++];
            }

            public static byte Read8(byte[] data, ref int i)
            {
                return data[i++];
            }

            public static byte[] ReadAllBytesFromStream(Stream input)
            {
                byte[] buffer = new byte[16 * 1024];
                using (MemoryStream ms = new MemoryStream())
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                    }

                    return ms.ToArray();
                }
            }

            public static string ReadString(byte[] data, ref int i, int length)
            {
                string result = Encoding.ASCII.GetString(data, i, length);
                i += length;
                return result;
            }

            public static int ReadVarInt(byte[] data, ref int i)
            {
                int result = (int)data[i++];

                if ((result & 0x80) == 0)
                {
                    return result;
                }

                result &= 0x7F;

                for (int j = 0; j < 3; j++)
                {
                    int value = (int)data[i++];

                    result = (result << 7) | (value & 0x7F);

                    if ((value & 0x80) == 0)
                    {
                        break;
                    }
                }

                return result;
            }
        }
    }

    public class MidiTrack
    {
        public int Index;

        public List<MidiEvent> MidiEvents = new List<MidiEvent>();

        public List<TextEvent> TextEvents = new List<TextEvent>();
    }

    public struct MidiEvent
    {
        public int Time;

        public byte Type;

        public byte Arg1;

        public byte Arg2;

        public byte Arg3;
    }

    public struct TextEvent
    {
        public int Time;

        public byte Type;

        public string Value;
    }

    public enum MidiEventType : byte
    {
        NoteOff = 0x80,

        NoteOn = 0x90,

        KeyAfterTouch = 0xA0,

        ControlChange = 0xB0,

        ProgramChange = 0xC0,

        ChannelAfterTouch = 0xD0,

        PitchBendChange = 0xE0,

        MetaEvent = 0xFF
    }

    public enum ControlChangeType : byte
    {
        BankSelect = 0x00,

        Modulation = 0x01,

        Volume = 0x07,

        Balance = 0x08,

        Pan = 0x0A,

        Sustain = 0x40
    }

    public enum TextEventType : byte
    {
        Text = 0x01,

        TrackName = 0x03,

        Lyric = 0x05,
    }

    public enum MetaEventType : byte
    {
        Tempo = 0x51,

        TimeSignature = 0x58,

        KeySignature = 0x59
    }
}
#endregion

public class EntryPoint {
	public void FromVegas(Vegas vegas){
		/* todo: implement a gui to select midis... */

		MidiParser.MidiFile midi_file = new MidiParser.MidiFile(vegas.InstallationDirectory + "\\song.mid");
		List<TrackEvent> original_events = new List<TrackEvent>();

		/* find the first event; kind of ugly */
		if (vegas.Project.Tracks.Count < 1) {
			MessageBox.Show("You don't even have any tracks.");
			return;
		}
		foreach (Track track in vegas.Project.Tracks) {
			if (track.Events.Count < 1)
				continue;
			foreach (TrackEvent track_event in track.Events) {
				original_events.Add(track_event);
			}
		}

		if (original_events.Count < 1) {
			MessageBox.Show("There are no events on the track(s)!");
			return;
		}

		List<TrackEvent> copied_events = new List<TrackEvent>(); // this will be used later for noteoffs

		foreach (MidiParser.MidiTrack track in midi_file.Tracks) {
			foreach (MidiParser.MidiEvent midi_event in midi_file.Tracks.MidiEvents) {
				switch (midi_event.Type) {
					case (byte)MidiParser.MidiEventType.NoteOn:
						// arg1 = channel
						// arg2 = note
						// arg3 = velocity (todo: implement!)
						for (int i = 0; i < copied_events.Count; i++) {
							copied_events[i].Length = Timecode.FromMilliseconds(midi_event.Time - copied_events[i].Start.ToMilliseconds());
							copied_events.RemoveAt(i);
						}
						foreach (TrackEvent track_event in original_events[0].Group) {
							copied_events.Add(track_event.Copy(track_event.Track, Timecode.FromMilliseconds(midi_event.Time)));
						}
						break;
					case (byte)MidiParser.MidiEventType.NoteOff:
						break;
					default:
						break;
				}
			}
		}

		foreach (TrackEvent original_event in original_events) {
			vegas.Project.Tracks[original_event.Track.Index].Events.Remove(original_event);
		}
	}
}

using Xunit;

using System.Text;
using Google.Protobuf;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Tests;

/// <summary>
/// D-19: <c>Hello</c>, <c>Heartbeat</c>, <c>Job{self_update}</c> and <c>JobResult</c> are
/// frozen at wire version 1. The "v1 serializer" here is a hand-rolled protobuf writer
/// that knows only the v1 field numbers and types; if anyone renumbers, retypes or removes
/// one of those fields, the bytes stop matching and this test fails before a release does.
/// </summary>
public sealed class FrozenSubsetTests
{
    [Fact]
    public void A_v1_hello_is_read_by_the_current_parser_and_written_back_byte_for_byte()
    {
        var v1 = new V1Writer()
            .String(1, "8f14e45f-ceea-467a-9575-28263f0c9b41")   // agent_id
            .String(2, "1c383cd3-0b7c-4d69-9a37-4bfd08bb6c2e")   // lab_id
            .Varint(3, 7)                                        // number
            .String(4, "0.1.0")                                  // agent_version
            .Varint(5, 1)                                        // protocol_version
            .String(6, "7FF688BA9B42085222CCD672F16A490F")       // certificate_serial
            .String(7, "02:00:5E:00:00:07")                      // mac
            .Varint(8, 1_725_450_000)                            // boot_time_unix
            .ToArray();

        var hello = Hello.Parser.ParseFrom(v1);

        Assert.Equal("8f14e45f-ceea-467a-9575-28263f0c9b41", hello.AgentId);
        Assert.Equal("1c383cd3-0b7c-4d69-9a37-4bfd08bb6c2e", hello.LabId);
        Assert.Equal(7, hello.Number);
        Assert.Equal("0.1.0", hello.AgentVersion);
        Assert.Equal(Defaults.FrozenProtocolVersion, hello.ProtocolVersion);
        Assert.Equal("7FF688BA9B42085222CCD672F16A490F", hello.CertificateSerial);
        Assert.Equal("02:00:5E:00:00:07", hello.Mac);
        Assert.Equal(1_725_450_000, hello.BootTimeUnix);

        // Fields added since v1 (update_state, previous_instance_id) stay at their defaults,
        // and a current build writing only v1 fields produces exactly the v1 bytes.
        Assert.Null(hello.UpdateState);
        Assert.Equal(string.Empty, hello.PreviousInstanceId);
        Assert.Equal(v1, hello.ToByteArray());
    }

    [Fact]
    public void A_hello_with_fields_added_after_v1_is_still_read_by_a_v1_reader()
    {
        var current = new Hello
        {
            AgentId = "a", LabId = "l", Number = 3, AgentVersion = "9.9.9", ProtocolVersion = 99,
            PreviousInstanceId = "some-console",
            UpdateState = new UpdateState { Phase = UpdateState.Types.Phase.OnProbation, ProbationEndsUnix = 5 },
        };

        // A v1 reader knows fields 1..8 only and must skip the rest without complaint.
        var fields = V1Reader.Read(current.ToByteArray());

        Assert.Equal("a", fields.Strings[1]);
        Assert.Equal("l", fields.Strings[2]);
        Assert.Equal(3UL, fields.Varints[3]);
        Assert.Equal(99UL, fields.Varints[5]);
        Assert.Contains(9, fields.Skipped);
        Assert.Contains(10, fields.Skipped);
    }

    [Fact]
    public void A_v1_heartbeat_round_trips()
    {
        var v1 = new V1Writer().Varint(1, 1_725_450_000).ToArray();
        var heartbeat = Heartbeat.Parser.ParseFrom(v1);

        Assert.Equal(1_725_450_000, heartbeat.SentAtUnix);
        Assert.Equal(v1, heartbeat.ToByteArray());
    }

    [Fact]
    public void A_v1_self_update_job_round_trips_including_its_args_map()
    {
        // map<string,string> is wire-encoded as repeated entries {1: key, 2: value}.
        var entry = new V1Writer().String(1, "version").String(2, "0.2.0").ToArray();
        var v1 = new V1Writer()
            .String(1, "job-1")                    // id
            .Varint(2, 10)                         // kind = SELF_UPDATE
            .Bytes(3, entry)                       // args
            .Varint(4, 600)                        // timeout_seconds
            .ToArray();

        var job = Job.Parser.ParseFrom(v1);

        Assert.Equal("job-1", job.Id);
        Assert.Equal(Job.Types.Kind.SelfUpdate, job.Kind);
        Assert.Equal("0.2.0", job.Args["version"]);
        Assert.Equal(600, job.TimeoutSeconds);
        Assert.Equal(v1, job.ToByteArray());
        Assert.Equal(10, (int)Job.Types.Kind.SelfUpdate);
    }

    [Fact]
    public void A_v1_job_result_round_trips()
    {
        var v1 = new V1Writer()
            .String(1, "job-1")          // job_id
            .Varint(2, 1)                // ok
            .Varint(3, 3)                // exit_code
            .String(4, "exit 3")         // message
            .String(5, "upload/1")       // artifact_ref
            .ToArray();

        var result = JobResult.Parser.ParseFrom(v1);

        Assert.Equal("job-1", result.JobId);
        Assert.True(result.Ok);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("exit 3", result.Message);
        Assert.Equal("upload/1", result.ArtifactRef);
        Assert.Equal(v1, result.ToByteArray());
    }

    /// <summary>The wire format of protobuf, by hand: enough of it to write the frozen subset.</summary>
    private sealed class V1Writer
    {
        private readonly MemoryStream _bytes = new();

        public V1Writer Varint(int field, ulong value)
        {
            WriteVarint((ulong)(field << 3) | 0);
            WriteVarint(value);
            return this;
        }

        public V1Writer String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public V1Writer Bytes(int field, byte[] value)
        {
            WriteVarint((ulong)(field << 3) | 2);
            WriteVarint((ulong)value.Length);
            _bytes.Write(value);
            return this;
        }

        public byte[] ToArray() => _bytes.ToArray();

        private void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            _bytes.WriteByte((byte)value);
        }
    }

    /// <summary>A reader that knows only what v1 knew (fields 1–8 of Hello) and skips the rest.</summary>
    private sealed class V1Reader
    {
        public Dictionary<int, string> Strings { get; } = [];

        public Dictionary<int, ulong> Varints { get; } = [];

        public HashSet<int> Skipped { get; } = [];

        public static V1Reader Read(byte[] bytes)
        {
            var reader = new V1Reader();
            var position = 0;

            while (position < bytes.Length)
            {
                var tag = ReadVarint(bytes, ref position);
                var field = (int)(tag >> 3);
                var wireType = (int)(tag & 7);

                switch (wireType)
                {
                    case 0:
                        var value = ReadVarint(bytes, ref position);
                        if (field <= 8)
                        {
                            reader.Varints[field] = value;
                        }
                        else
                        {
                            reader.Skipped.Add(field);
                        }

                        break;

                    case 2:
                        var length = (int)ReadVarint(bytes, ref position);
                        if (field <= 8)
                        {
                            reader.Strings[field] = Encoding.UTF8.GetString(bytes, position, length);
                        }
                        else
                        {
                            reader.Skipped.Add(field);
                        }

                        position += length;
                        break;

                    default:
                        throw new InvalidDataException($"wire type {wireType} is not used by the frozen subset");
                }
            }

            return reader;
        }

        private static ulong ReadVarint(byte[] bytes, ref int position)
        {
            ulong result = 0;
            var shift = 0;
            while (true)
            {
                var b = bytes[position++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }
        }
    }
}

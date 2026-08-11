using HealthBridge.HL7Service.Builders;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Builders;

/// <summary>
/// Tests for HL7 v2 ACK/NACK generation.
///
/// Verified against HL7 v2.5:
///   ACK structure — https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ACK
///   MSA-1 codes, table 0008 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70008
///   ERR codes, table 0357 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70357
/// </summary>
public class Hl7AckBuilderTests
{
    private readonly Hl7AckBuilder _builder = new();

    // A parsed view of an ACK, so assertions read in HL7 terms rather than string indexes.
    private static (string[] Segments, string[] MshFields, string[] MsaFields) Parse(string ack)
    {
        var segments = ack.Split('\r');
        return (segments,
                segments[0].Split('|'),
                segments[1].Split('|'));
    }

    [Fact]
    public void Success_produces_MSA_1_of_AA()
    {
        var ack = _builder.BuildAck("MSG001", success: true);

        var (_, _, msa) = Parse(ack);
        Assert.Equal("MSA", msa[0]);
        Assert.Equal("AA", msa[1]);   // AA = Application Accept (table 0008)
    }

    [Fact]
    public void Failure_produces_MSA_1_of_AE()
    {
        var ack = _builder.BuildAck("MSG001", success: false, errorDetail: "boom");

        var (_, _, msa) = Parse(ack);
        Assert.Equal("AE", msa[1]);   // AE = Application Error (table 0008)
    }

    [Fact]
    public void MSA_2_echoes_the_original_message_control_id()
    {
        // The sender correlates the ACK to its message by MSA-2 — if this regresses,
        // every downstream interface engine loses track of its outbound messages.
        var ack = _builder.BuildAck("CTRL-42", success: true);

        var (_, _, msa) = Parse(ack);
        Assert.Equal("CTRL-42", msa[2]);
    }

    [Fact]
    public void Segments_are_separated_by_carriage_return_only()
    {
        var ack = _builder.BuildAck("MSG001", success: true);

        Assert.Contains('\r', ack);
        Assert.DoesNotContain('\n', ack);   // HL7 v2.5 Ch. 2.5 — \r is the terminator
    }

    [Fact]
    public void Successful_ack_has_exactly_MSH_and_MSA_with_no_trailing_empty_segment()
    {
        var ack = _builder.BuildAck("MSG001", success: true);

        var (segments, msh, _) = Parse(ack);
        Assert.Equal(2, segments.Length);
        Assert.Equal("MSH", msh[0]);
        Assert.Equal("MSA", segments[1].Split('|')[0]);
        Assert.False(ack.EndsWith('\r'));
    }

    [Fact]
    public void Error_detail_appends_an_ERR_segment_with_the_0357_code()
    {
        var ack = _builder.BuildAck("MSG001", success: false, errorDetail: "Segment PID missing");

        var (segments, _, _) = Parse(ack);
        Assert.Equal(3, segments.Length);

        var err = segments[2];
        Assert.StartsWith("ERR", err);
        Assert.Contains("207", err);        // 207 = Application internal error (table 0357)
        Assert.Contains("HL70357", err);
        Assert.Contains("Segment PID missing", err);
    }

    [Fact]
    public void Failure_without_error_detail_omits_the_ERR_segment()
    {
        // ERR is optional in the ACK structure — a NACK with no detail should not
        // emit an empty ERR segment that a strict receiver would reject.
        var ack = _builder.BuildAck("MSG001", success: false);

        var (segments, _, _) = Parse(ack);
        Assert.Equal(2, segments.Length);
        Assert.DoesNotContain("ERR", ack);
    }

    [Fact]
    public void MSH_declares_the_standard_HL7_encoding_characters()
    {
        var ack = _builder.BuildAck("MSG001", success: true);

        var (_, msh, _) = Parse(ack);
        Assert.Equal("MSH", msh[0]);
        Assert.Equal(@"^~\&", msh[1]);   // MSH-2 encoding characters
    }

    [Fact]
    public void MSH_9_declares_the_message_type_as_ACK()
    {
        var ack = _builder.BuildAck("MSG001", success: true);

        var (_, msh, _) = Parse(ack);
        Assert.Equal("ACK", msh[8]);     // MSH-9 message type
    }

    [Fact]
    public void MSH_7_timestamp_is_14_digit_HL7_format()
    {
        var ack = _builder.BuildAck("MSG001", success: true);

        var (_, msh, _) = Parse(ack);
        var timestamp = msh[6];          // MSH-7 date/time of message
        Assert.Matches(@"^\d{14}$", timestamp);
    }

    [Fact]
    public void MSA_3_carries_the_error_text_on_failure_and_an_accept_message_on_success()
    {
        var accepted = _builder.BuildAck("MSG001", success: true);
        var rejected = _builder.BuildAck("MSG001", success: false, errorDetail: "bad PID");

        Assert.Equal("Message accepted", Parse(accepted).MsaFields[3]);
        Assert.Equal("bad PID", Parse(rejected).MsaFields[3]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    public void Missing_or_unknown_message_ids_still_produce_a_well_formed_ack(string messageId)
    {
        // The controller passes "UNKNOWN" when parsing failed before MSH-10 could be read,
        // so this path runs on every malformed inbound message.
        var ack = _builder.BuildAck(messageId, success: false, errorDetail: "unparseable");

        var (segments, msh, msa) = Parse(ack);
        Assert.Equal("MSH", msh[0]);
        Assert.Equal("AE", msa[1]);
        Assert.Equal(messageId, msa[2]);
        Assert.Equal(3, segments.Length);
    }

    [Fact]
    public void Version_and_processing_id_are_hardcoded__documented_conformance_gap()
    {
        // Pins the known gap in docs/STANDARDS.md §6: MSH-11 and MSH-12 should mirror the
        // inbound message rather than always claiming production/2.5. If this test starts
        // failing because someone made them dynamic, delete it and close the gap entry.
        var ack = _builder.BuildAck("MSG001", success: true);

        var (_, msh, _) = Parse(ack);
        Assert.Equal("P", msh[10]);    // MSH-11 processing ID (table 0103)
        Assert.Equal("2.5", msh[11]);  // MSH-12 version ID
    }
}

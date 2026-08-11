// Named constants for values that appear in more than one place, or that come
// from a specification rather than from us.
//
// The two "unknown" placeholders below are deliberately spelled differently, and
// that is the main reason this file exists. HL7 wire values are uppercase by
// convention; audit-log text is read by humans. Keeping them adjacent makes the
// difference intentional rather than something the next reader guesses at.

namespace HealthBridge.HL7Service;

/// <summary>
/// HL7 v2 wire-format constants. See docs/STANDARDS.md for the clause each maps to.
/// </summary>
public static class Hl7Constants
{
    /// <summary>
    /// Segment terminator. HL7 v2.5 chapter 2.5 defines carriage return (0x0D) —
    /// not \n, and not \r\n. Senders that use \n are non-conformant; we normalise
    /// them on the way in rather than rejecting the message.
    /// </summary>
    public const string SegmentSeparator = "\r";

    /// <summary>
    /// Same value as <see cref="SegmentSeparator"/>, for APIs that take a char
    /// (for example string.TrimEnd).
    /// </summary>
    public const char SegmentSeparatorChar = '\r';

    /// <summary>
    /// Placeholder for MSH-10 (Message Control ID) when an inbound message omits
    /// it. Uppercase because this value travels on the wire inside an ACK.
    /// Deliberately NOT the same as <see cref="AuditConstants.UnknownValue"/>.
    /// </summary>
    public const string UnknownMessageId = "UNKNOWN";
}

/// <summary>
/// Placeholders written into the PHI audit trail.
/// </summary>
public static class AuditConstants
{
    /// <summary>
    /// Used when an audit field cannot be determined. Mixed case because it is
    /// read by a human reviewing an audit log, and never sent over HL7.
    /// Deliberately NOT the same as <see cref="Hl7Constants.UnknownMessageId"/>.
    /// </summary>
    public const string UnknownValue = "Unknown";
}

/// <summary>
/// MIME types this service produces and consumes. These are const rather than
/// static readonly because [Produces] and [Consumes] attributes require
/// compile-time constants.
/// </summary>
public static class ContentTypes
{
    public const string Json = "application/json";

    /// <summary>Raw HL7 v2 messages are posted as plain text.</summary>
    public const string PlainText = "text/plain";

    /// <summary>FHIR R4 JSON representation — FHIR R4 section 2.21.2.</summary>
    public const string FhirJson = "application/fhir+json";
}

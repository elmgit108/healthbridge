namespace HealthBridge.HL7Service.Tests.TestData;

/// <summary>
/// Sample HL7 v2.5 messages used across the test suite.
///
/// Every message is built with Join() so the segment terminator is a literal \r —
/// HL7 v2.5 Ch. 2.5 requires carriage return, and a test written with \n would be
/// testing our normalization rather than the parser. See docs/STANDARDS.md §1.4.
///
/// All patient data here is fictional.
/// </summary>
public static class Hl7Samples
{
    /// <summary>Joins segments with the HL7 segment terminator (\r, 0x0D).</summary>
    public static string Join(params string[] segments) => string.Join("\r", segments);

    /// <summary>ADT^A01 admission — Smith, John. ICU room 101, emergency admission.</summary>
    public const string AdtA01MessageId = "MSG001";

    public static string AdtA01() => Join(
        @"MSH|^~\&|HospitalEMR|MainHospital|HealthBridge|CLOUD|20240115120000||ADT^A01|MSG001|P|2.5",
        "EVN|A01|20240115120000",
        "PID|1||PAT001^^^MRN||Smith^John^A||19800315|M|||123 King St^^Toronto^ON^M5H2N2^CA",
        "PV1|1|I|ICU^101^A|E|||DOC01^House^Gregory");

    /// <summary>ADT^A01 with only the segments HL7 marks as required — no PV1 detail, no address.</summary>
    public static string AdtA01Minimal() => Join(
        @"MSH|^~\&|HospitalEMR|MainHospital|HealthBridge|CLOUD|20240115120000||ADT^A01|MSG-MIN|P|2.5",
        "EVN|A01|20240115120000",
        "PID|1||PAT999^^^MRN||Doe^Jane||19900101|F",
        "PV1|1|O");

    /// <summary>ADT^A08 update — nHapi maps A08 onto the same ADT_A01 structure in v2.5.</summary>
    public static string AdtA08() => Join(
        @"MSH|^~\&|HospitalEMR|MainHospital|HealthBridge|CLOUD|20240115140000||ADT^A08|MSG003|P|2.5",
        "EVN|A08|20240115140000",
        "PID|1||PAT002^^^MRN||Garcia^Maria||19750620|F",
        "PV1|1|I|CARD^205^B|R");

    /// <summary>ORU^R01 lab result — one OBR with a single numeric OBX (WBC).</summary>
    public const string OruR01MessageId = "MSG002";

    public static string OruR01() => Join(
        @"MSH|^~\&|LabSystem|MainLab|HealthBridge|CLOUD|20240115130000||ORU^R01|MSG002|P|2.5",
        "PID|1||PAT002^^^MRN||Doe^Jane||19920720|F",
        "OBR|1|||CBC^Complete Blood Count",
        "OBX|1|NM|WBC^White Blood Cell Count||7.5|10*3/uL|4.5-11.0|N|||F");

    /// <summary>ORU^R01 with several OBX segments, including abnormal flags and a non-numeric result.</summary>
    public static string OruR01MultipleObservations() => Join(
        @"MSH|^~\&|LabSystem|MainLab|HealthBridge|CLOUD|20240115130000||ORU^R01|MSG004|P|2.5",
        "PID|1||PAT003^^^MRN||Chen^David||19850910|M",
        "OBR|1|||BMP^Basic Metabolic Panel",
        "OBX|1|NM|GLU^Glucose||145|mg/dL|70-100|H|||F",
        "OBX|2|NM|NA^Sodium||138|mmol/L|135-145|N|||F",
        "OBX|3|NM|K^Potassium||2.9|mmol/L|3.5-5.1|LL|||F",
        // ST (string) value type — legal HL7, but not translated to FHIR today.
        // See docs/STANDARDS.md §6.
        "OBX|4|ST|COMMENT^Lab Comment||Hemolyzed sample||||||F");

    /// <summary>A message type with no dedicated extractor — exercises DefaultExtractorStrategy.</summary>
    public static string SiuS12() => Join(
        @"MSH|^~\&|SchedApp|MainHospital|HealthBridge|CLOUD|20240115150000||SIU^S12|MSG005|P|2.5",
        "SCH|1||||||Normal|Routine|30|min");

    /// <summary>Not an HL7 message at all — used for parse-failure paths.</summary>
    public const string Garbage = "this is definitely not an HL7 message";

    /// <summary>Well-formed segments but no MSH — HL7 requires MSH first.</summary>
    public static string MissingMsh() => Join(
        "PID|1||PAT001^^^MRN||Smith^John||19800315|M");
}

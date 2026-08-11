namespace HealthBridge.HL7Service.Models;

/// <summary>
/// Wrapper returned by every HL7 parse request. Includes the parsed patient data,
/// the generated ACK message, and success/error metadata.
///
/// Sources:
///   MSH segment (MSH-9 message type, MSH-10 control ID) —
///     https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/MSH
///   ACK message structure (MSH + MSA + optional ERR) —
///     https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ACK
///   Full standards map: docs/STANDARDS.md
/// </summary>
public class HL7ParseResult
{
    public bool Success { get; set; }
    public string? MessageId { get; set; }      // MSH-10 control ID from the original message
    public string? MessageType { get; set; }     // e.g. "ADT_A01", "ORU_R01"
    public PatientData? Patient { get; set; }    // Extracted patient demographics
    public string? Error { get; set; }           // Error detail if parsing failed
    public string? Acknowledgement { get; set; } // Generated HL7 ACK/NACK message
}

/// <summary>
/// Patient demographics extracted from HL7 PID and PV1 segments.
/// Fields map directly to standard HL7 v2 segment fields.
///
/// Sources (HL7 v2.5 — the normative spec is the HL7 International PDF;
/// the links below are the free Caristix rendering of the same definitions):
///   MSH segment — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/MSH
///   PID segment — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/PID
///   PV1 segment — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/PV1
///
/// Note: several of these fields repeat in the standard (PID-3, PID-5, PV1-7);
/// we read only the first repetition. See docs/STANDARDS.md §6 for the gap list.
/// </summary>
public class PatientData
{
    public string MessageId { get; set; } = "";      // MSH-10
    public string SendingApp { get; set; } = "";      // MSH-3 (e.g. "HospitalEMR")
    public string PatientId { get; set; } = "";       // PID-3 — Medical Record Number (MRN), CX data type
    public string FirstName { get; set; } = "";       // PID-5.2 — XPN.2 given name
    public string LastName { get; set; } = "";        // PID-5.1 — XPN.1 family name
    public string DateOfBirth { get; set; } = "";     // PID-7 (TS format: yyyyMMdd[HHmmss])
    public string Gender { get; set; } = "";          // PID-8 — HL7 table 0001 (F/M/O/U/A/N)
                                                      //   https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70001
    public string Ward { get; set; } = "";            // PV1-3.2 — room component of the PL location
                                                      //   https://hl7-definition.caristix.com/v2/HL7v2.5/DataTypes/PL
    public string AdmissionType { get; set; } = "";   // PV1-4 — HL7 table 0007 (A/C/E/L/N/R/U)
                                                      //   https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70007
}

/// <summary>
/// DICOM metadata extracted from a .dcm file or received as JSON.
/// Fields correspond to standard DICOM tags used in radiology workflows.
///
/// Sources (DICOM PS3 is published free by NEMA — these links are normative):
///   Data dictionary (every tag, VR, VM) — PS3.6 §6:
///     https://dicom.nema.org/medical/dicom/current/output/chtml/part06/chapter_6.html
///   Patient module — PS3.3 C.7.1.1:
///     https://dicom.nema.org/medical/dicom/current/output/chtml/part03/sect_C.7.html#sect_C.7.1.1
///   General Study module — PS3.3 C.7.2.1:
///     https://dicom.nema.org/medical/dicom/current/output/chtml/part03/sect_C.7.2.html#sect_C.7.2.1
///   Value representations (PN, DA, UI, US) — PS3.5 §6.2:
///     https://dicom.nema.org/medical/dicom/current/output/chtml/part05/sect_6.2.html
///   Cross-check browser (unofficial): https://dicom.innolitics.com/ciods
/// </summary>
public class DicomMetadata
{
    // PN value representation — Family^Given^Middle^Prefix^Suffix (PS3.5 §6.2)
    public string PatientName { get; set; } = "";        // (0010,0010) — Type 2: required, may be empty
    public string PatientId { get; set; } = "";          // (0010,0020) — Type 2, hospital MRN
    public string StudyDate { get; set; } = "";          // (0008,0020) — DA format: yyyyMMdd
    // Modality values are *defined terms*, not a closed enumeration — PS3.3 C.7.3.1.1.1:
    //   https://dicom.nema.org/medical/dicom/current/output/chtml/part03/sect_C.7.3.html#sect_C.7.3.1.1.1
    public string Modality { get; set; } = "";           // (0008,0060) — CT, MR, DX, US, etc.
    public string StudyDescription { get; set; } = "";   // (0008,1030) — Type 3 (optional)
    public string SeriesDescription { get; set; } = "";  // (0008,103E) — Type 3 (optional)
    public string InstitutionName { get; set; } = "";    // (0008,0080) — Type 3 (optional)
    // UID encoding rules (≤64 chars, numeric components under a registered root) — PS3.5 §9:
    //   https://dicom.nema.org/medical/dicom/current/output/chtml/part05/chapter_9.html
    public string StudyInstanceUid { get; set; } = "";   // (0020,000D) — Type 1: required, non-empty
    // SOP Class UID registry — PS3.6 Annex A:
    //   https://dicom.nema.org/medical/dicom/current/output/chtml/part06/chapter_A.html
    public string SOPClassUid { get; set; } = "";        // (0008,0016) — identifies image type
    // Image Pixel module — PS3.3 C.7.6.3 (both are US, so max 65535):
    //   https://dicom.nema.org/medical/dicom/current/output/chtml/part03/sect_C.7.6.3.html
    public int Rows { get; set; }                        // (0028,0010) — image height in pixels
    public int Columns { get; set; }                     // (0028,0011) — image width in pixels
}

/// <summary>
/// JSON input model for the /api/dicom/metadata endpoint.
/// Allows testing DICOM metadata parsing without needing a real .dcm binary file.
///
/// This is a project-local convenience shape, not the DICOM JSON Model. If interop with
/// other DICOM tooling is ever needed, conform to PS3.18 Annex F instead:
///   https://dicom.nema.org/medical/dicom/current/output/chtml/part18/chapter_F.html
/// </summary>
public class DicomJsonInput
{
    public string? PatientName { get; set; }
    public string? PatientId { get; set; }
    public string? StudyDate { get; set; }
    public string? Modality { get; set; }
    public string? StudyDescription { get; set; }
    public string? InstitutionName { get; set; }
    public string? StudyInstanceUid { get; set; }
}

/// <summary>
/// Health check response model — used by the Go gateway to verify this service is alive.
/// Follows the project convention: { status, service, timestamp }.
/// </summary>
public class HealthStatus
{
    public string Service { get; set; } = "HealthBridge HL7/DICOM Service";
    public string Status { get; set; } = "healthy";
    public string Version { get; set; } = "1.0.0";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

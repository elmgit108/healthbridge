using FellowOakDicom;
using HealthBridge.HL7Service.Models;

namespace HealthBridge.HL7Service.Services;

/// <summary>
/// Service for extracting metadata from DICOM medical imaging files.
/// </summary>
public interface IDicomService
{
    /// <summary>Parse a binary .dcm file stream and extract metadata tags.</summary>
    DicomMetadata ExtractMetadata(Stream fileStream);

    /// <summary>Convert a JSON representation of DICOM tags (for testing without real .dcm files).</summary>
    DicomMetadata ExtractFromJson(DicomJsonInput input);
}

/// <summary>
/// DICOM metadata extraction using the fo-dicom (Fellow Oak DICOM) library.
///
/// DICOM (Digital Imaging and Communications in Medicine) is the universal standard
/// for medical imaging. Every .dcm file contains both pixel data (the image) and
/// a header with structured metadata tags — patient name, study date, modality, etc.
///
/// fo-dicom reads the binary DICOM format and exposes tags via a typed API.
/// This service extracts the most clinically relevant tags for downstream use.
///
/// Sources (DICOM PS3 is published free by NEMA — normative):
///   Standard index      — https://dicom.nema.org/medical/dicom/current/output/chtml/
///   Data dictionary     — PS3.6 §6:  .../part06/chapter_6.html
///   Patient module      — PS3.3 C.7.1.1
///   General Study       — PS3.3 C.7.2.1
///   Value representations — PS3.5 §6.2
///   File format (preamble + "DICM" + File Meta Information) — PS3.10 §7:
///     https://dicom.nema.org/medical/dicom/current/output/chtml/part10/chapter_7.html
/// Per-tag links are on the DicomMetadata model; see also docs/STANDARDS.md §2.
///
/// Library (implementation, not authority): https://github.com/fo-dicom/fo-dicom
/// </summary>
public class DicomService : IDicomService
{
    private readonly ILogger<DicomService> _logger;

    public DicomService(ILogger<DicomService> logger)
    {
        _logger = logger;
    }

    public DicomMetadata ExtractMetadata(Stream fileStream)
    {
        try
        {
            // fo-dicom parses the binary DICOM file format (128-byte preamble, "DICM" magic,
            // File Meta Information, then the dataset) — PS3.10 §7:
            //   https://dicom.nema.org/medical/dicom/current/output/chtml/part10/chapter_7.html
            var file = DicomFile.Open(fileStream);
            var dataset = file.Dataset;

            // Extract standard DICOM tags — each tag is a (group,element) pair defined in
            // the PS3.6 data dictionary:
            //   https://dicom.nema.org/medical/dicom/current/output/chtml/part06/chapter_6.html
            var metadata = new DicomMetadata
            {
                PatientName       = dataset.GetSingleValueOrDefault(DicomTag.PatientName, ""),       // (0010,0010)
                PatientId         = dataset.GetSingleValueOrDefault(DicomTag.PatientID, ""),         // (0010,0020)
                StudyDate         = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, ""),         // (0008,0020)
                Modality          = dataset.GetSingleValueOrDefault(DicomTag.Modality, ""),          // (0008,0060)
                StudyDescription  = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, ""),  // (0008,1030)
                SeriesDescription = dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, ""), // (0008,103E)
                InstitutionName   = dataset.GetSingleValueOrDefault(DicomTag.InstitutionName, ""),   // (0008,0080)
                StudyInstanceUid  = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, ""),  // (0020,000D)
                SOPClassUid       = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, ""),       // (0008,0016)
                Rows              = dataset.GetSingleValueOrDefault(DicomTag.Rows, 0),               // (0028,0010)
                Columns           = dataset.GetSingleValueOrDefault(DicomTag.Columns, 0)             // (0028,0011)
            };

            _logger.LogInformation("Extracted DICOM metadata for patient {Id}, modality {Mod}",
                metadata.PatientId, metadata.Modality);

            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse DICOM file: {Error}", ex.Message);
            throw new InvalidOperationException($"Invalid DICOM file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Accepts DICOM metadata as JSON — useful for testing and demos without a real .dcm binary.
    /// Generates a random StudyInstanceUID if none is provided.
    ///
    /// Conformance warning: a GUID is NOT a valid DICOM UID. PS3.5 §9 requires dot-separated
    /// numeric components of at most 64 characters under a registered organizational root:
    ///   https://dicom.nema.org/medical/dicom/current/output/chtml/part05/chapter_9.html
    /// A real PACS would reject this value. Acceptable for the POC only — see docs/STANDARDS.md §6.
    /// </summary>
    public DicomMetadata ExtractFromJson(DicomJsonInput input)
    {
        return new DicomMetadata
        {
            PatientName      = input.PatientName ?? "",
            PatientId        = input.PatientId ?? "",
            StudyDate        = input.StudyDate ?? "",
            Modality         = input.Modality ?? "",
            StudyDescription = input.StudyDescription ?? "",
            InstitutionName  = input.InstitutionName ?? "",
            StudyInstanceUid = input.StudyInstanceUid ?? Guid.NewGuid().ToString()
        };
    }
}

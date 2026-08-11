using FellowOakDicom;

namespace HealthBridge.HL7Service.Tests.TestData;

/// <summary>
/// Builds valid in-memory DICOM files so the DICOM tests need no binary fixtures
/// on disk and no network download.
///
/// A conformant Part 10 file needs the File Meta Information group, which fo-dicom
/// derives from SOPClassUID / SOPInstanceUID / TransferSyntax — so those three are
/// always set here. PS3.10 §7:
///   https://dicom.nema.org/medical/dicom/current/output/chtml/part10/chapter_7.html
///
/// All patient data here is fictional.
/// </summary>
public static class DicomSamples
{
    public const string PatientName = "Smith^John";
    public const string PatientId = "PAT001";
    public const string StudyDate = "20240115";
    public const string StudyInstanceUid = "1.2.840.113619.2.55.3.604688119.971.1234567890.1";

    /// <summary>A CT study with every tag DicomService extracts populated.</summary>
    public static Stream FullCtStudy()
    {
        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
            { DicomTag.SOPInstanceUID, DicomUID.Generate() },
            { DicomTag.PatientName, PatientName },
            { DicomTag.PatientID, PatientId },
            { DicomTag.StudyDate, StudyDate },
            { DicomTag.Modality, "CT" },
            { DicomTag.StudyDescription, "Chest CT without contrast" },
            { DicomTag.SeriesDescription, "Axial 1mm" },
            { DicomTag.InstitutionName, "Toronto General Hospital" },
            { DicomTag.StudyInstanceUID, StudyInstanceUid },
            { DicomTag.SeriesInstanceUID, DicomUID.Generate() },
            // Rows/Columns are VR "US" (unsigned short) — PS3.3 C.7.6.3
            { DicomTag.Rows, (ushort)512 },
            { DicomTag.Columns, (ushort)512 },
        };

        return ToStream(dataset);
    }

    /// <summary>
    /// A study carrying only the tags DICOM marks Type 1/Type 2 for these modules.
    /// Type 3 tags (StudyDescription, SeriesDescription, InstitutionName) and the
    /// pixel-matrix tags are absent — all legal, and the service must not throw.
    /// </summary>
    public static Stream MinimalStudy()
    {
        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUID.Generate() },
            // PatientName/PatientID are Type 2: required to be present, allowed to be empty
            { DicomTag.PatientName, string.Empty },
            { DicomTag.PatientID, string.Empty },
            { DicomTag.StudyInstanceUID, StudyInstanceUid },
            { DicomTag.Modality, "OT" },
        };

        return ToStream(dataset);
    }

    /// <summary>Bytes that are not a DICOM file — exercises the parse-failure path.</summary>
    public static Stream NotADicomFile() =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes("PK this is a zip, not a dcm"));

    private static Stream ToStream(DicomDataset dataset)
    {
        var file = new DicomFile(dataset);
        var stream = new MemoryStream();
        file.Save(stream);
        stream.Position = 0;
        return stream;
    }
}

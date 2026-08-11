using HealthBridge.HL7Service.Models;
using HealthBridge.HL7Service.Services;
using HealthBridge.HL7Service.Tests.TestData;
using HealthBridge.HL7Service.Tests.TestDoubles;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Services;

/// <summary>
/// Tests for DICOM metadata extraction.
///
/// Tag numbers and types verified against DICOM PS3.6 §6 and PS3.3 C.7:
///   https://dicom.nema.org/medical/dicom/current/output/chtml/part06/chapter_6.html
/// </summary>
public class DicomServiceTests
{
    private static DicomService BuildService() =>
        new(TestHelpers.NullLoggerFor<DicomService>());

    // --- Binary .dcm parsing --------------------------------------------------

    [Fact]
    public void Extracts_every_mapped_tag_from_a_full_study()
    {
        using var stream = DicomSamples.FullCtStudy();

        var metadata = BuildService().ExtractMetadata(stream);

        Assert.Equal(DicomSamples.PatientName, metadata.PatientName);        // (0010,0010)
        Assert.Equal(DicomSamples.PatientId, metadata.PatientId);            // (0010,0020)
        Assert.Equal(DicomSamples.StudyDate, metadata.StudyDate);            // (0008,0020)
        Assert.Equal("CT", metadata.Modality);                               // (0008,0060)
        Assert.Equal("Chest CT without contrast", metadata.StudyDescription);// (0008,1030)
        Assert.Equal("Axial 1mm", metadata.SeriesDescription);               // (0008,103E)
        Assert.Equal("Toronto General Hospital", metadata.InstitutionName);  // (0008,0080)
        Assert.Equal(DicomSamples.StudyInstanceUid, metadata.StudyInstanceUid); // (0020,000D)
        Assert.Equal(512, metadata.Rows);                                    // (0028,0010)
        Assert.Equal(512, metadata.Columns);                                 // (0028,0011)
    }

    [Fact]
    public void Reads_the_SOP_class_uid_that_identifies_the_image_type()
    {
        using var stream = DicomSamples.FullCtStudy();

        var metadata = BuildService().ExtractMetadata(stream);

        // 1.2.840.10008.5.1.4.1.1.2 = CT Image Storage, per the PS3.6 Annex A registry
        Assert.Equal("1.2.840.10008.5.1.4.1.1.2", metadata.SOPClassUid);
    }

    [Fact]
    public void PatientName_keeps_the_DICOM_PN_caret_format()
    {
        // PN is Family^Given^Middle^Prefix^Suffix (PS3.5 §6.2). The service does not
        // split it, so downstream consumers must — pinning that expectation here.
        using var stream = DicomSamples.FullCtStudy();

        var metadata = BuildService().ExtractMetadata(stream);

        Assert.Contains('^', metadata.PatientName);
        Assert.Equal("Smith^John", metadata.PatientName);
    }

    [Fact]
    public void StudyDate_uses_the_same_yyyyMMdd_layout_as_HL7()
    {
        using var stream = DicomSamples.FullCtStudy();

        var metadata = BuildService().ExtractMetadata(stream);

        Assert.Matches(@"^\d{8}$", metadata.StudyDate);
    }

    [Fact]
    public void Absent_optional_tags_come_back_empty_rather_than_throwing()
    {
        // StudyDescription, SeriesDescription and InstitutionName are Type 3 (optional)
        // and Rows/Columns are absent outside image IODs — all legal DICOM.
        using var stream = DicomSamples.MinimalStudy();

        var metadata = BuildService().ExtractMetadata(stream);

        Assert.Equal("", metadata.StudyDescription);
        Assert.Equal("", metadata.SeriesDescription);
        Assert.Equal("", metadata.InstitutionName);
        Assert.Equal(0, metadata.Rows);
        Assert.Equal(0, metadata.Columns);
    }

    [Fact]
    public void Empty_Type_2_patient_tags_are_not_treated_as_an_error()
    {
        // PatientName and PatientID are Type 2: present but permitted to be empty.
        // An anonymised study is the normal case for this, not a corrupt file.
        using var stream = DicomSamples.MinimalStudy();

        var metadata = BuildService().ExtractMetadata(stream);

        Assert.Equal("", metadata.PatientName);
        Assert.Equal("", metadata.PatientId);
        Assert.Equal(DicomSamples.StudyInstanceUid, metadata.StudyInstanceUid);
    }

    [Fact]
    public void A_non_DICOM_file_raises_InvalidOperationException()
    {
        // The controller maps exactly this exception to 422; anything else becomes a 500.
        using var stream = DicomSamples.NotADicomFile();
        var service = BuildService();

        var ex = Assert.Throws<InvalidOperationException>(() => service.ExtractMetadata(stream));
        Assert.Contains("Invalid DICOM file", ex.Message);
    }

    [Fact]
    public void An_empty_stream_raises_InvalidOperationException()
    {
        using var stream = new MemoryStream();
        var service = BuildService();

        Assert.Throws<InvalidOperationException>(() => service.ExtractMetadata(stream));
    }

    [Fact]
    public void Parse_failures_preserve_the_underlying_cause()
    {
        using var stream = DicomSamples.NotADicomFile();
        var service = BuildService();

        var ex = Assert.Throws<InvalidOperationException>(() => service.ExtractMetadata(stream));
        Assert.NotNull(ex.InnerException);
    }

    // --- JSON input path ------------------------------------------------------

    [Fact]
    public void Json_input_maps_every_supplied_field()
    {
        var input = new DicomJsonInput
        {
            PatientName = "Doe^Jane",
            PatientId = "PAT002",
            StudyDate = "20240220",
            Modality = "MR",
            StudyDescription = "Brain MRI",
            InstitutionName = "Sunnybrook",
            StudyInstanceUid = "1.2.840.113619.2.55.3.1"
        };

        var metadata = BuildService().ExtractFromJson(input);

        Assert.Equal("Doe^Jane", metadata.PatientName);
        Assert.Equal("PAT002", metadata.PatientId);
        Assert.Equal("20240220", metadata.StudyDate);
        Assert.Equal("MR", metadata.Modality);
        Assert.Equal("Brain MRI", metadata.StudyDescription);
        Assert.Equal("Sunnybrook", metadata.InstitutionName);
        Assert.Equal("1.2.840.113619.2.55.3.1", metadata.StudyInstanceUid);
    }

    [Fact]
    public void Json_input_turns_nulls_into_empty_strings()
    {
        var metadata = BuildService().ExtractFromJson(new DicomJsonInput());

        Assert.Equal("", metadata.PatientName);
        Assert.Equal("", metadata.PatientId);
        Assert.Equal("", metadata.StudyDate);
        Assert.Equal("", metadata.Modality);
    }

    [Fact]
    public void Json_input_without_a_study_uid_generates_one()
    {
        var metadata = BuildService().ExtractFromJson(new DicomJsonInput());

        Assert.NotEmpty(metadata.StudyInstanceUid);
    }

    [Fact]
    public void Generated_study_uid_is_a_GUID_and_therefore_not_a_valid_DICOM_UID()
    {
        // Pins the conformance gap in docs/STANDARDS.md §6. PS3.5 §9 requires
        // dot-separated numeric components, max 64 chars, under a registered org root:
        //   https://dicom.nema.org/medical/dicom/current/output/chtml/part05/chapter_9.html
        // A GUID satisfies none of that, so a real PACS would reject this study.
        // When the fallback is fixed, this test should be inverted to assert conformance.
        var uid = BuildService().ExtractFromJson(new DicomJsonInput()).StudyInstanceUid;

        Assert.True(Guid.TryParse(uid, out _), "fallback UID is expected to be a GUID today");
        Assert.Contains('-', uid);                       // GUIDs contain hyphens; DICOM UIDs cannot
        Assert.DoesNotMatch(@"^[0-9]+(\.[0-9]+)+$", uid); // not the required numeric-dotted form
    }

    [Fact]
    public void Json_input_does_not_populate_the_binary_only_tags()
    {
        // DicomJsonInput carries no SeriesDescription, SOPClassUID, Rows or Columns,
        // so a JSON-sourced study is deliberately less complete than a parsed .dcm.
        var metadata = BuildService().ExtractFromJson(new DicomJsonInput { Modality = "US" });

        Assert.Equal("", metadata.SeriesDescription);
        Assert.Equal("", metadata.SOPClassUid);
        Assert.Equal(0, metadata.Rows);
        Assert.Equal(0, metadata.Columns);
    }
}

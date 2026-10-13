using System.Text;
using QuestionBank.Core.Extraction;

namespace QuestionBank.Tests.Extraction;

public sealed class TextQuestionReaderTests
{
    private static ReaderResult Read(string text) => new TextQuestionReader().Read(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void MarkerBeforeOption_IsDetected_AndTextIsPreserved()
    {
        var r = Read("""
            اختبار تجريبي - مقدمة تُتجاهل

            1. ما الوحدة الأساسية لقياس الزمن؟
            أ) الدقيقة
            *ب) الثانية
            ج) الساعة
            د) اليوم
            """);

        var q = Assert.Single(r.Questions);
        Assert.Equal("ما الوحدة الأساسية لقياس الزمن؟", q.Text);
        Assert.Equal(["الدقيقة", "الثانية", "الساعة", "اليوم"], q.Options);
        Assert.Equal(1, q.CorrectIndex);
        Assert.Equal(AnswerDetectors.Marker, q.Detector);
        Assert.Equal(ExtractionConfidence.High, q.Confidence);
        Assert.Equal("سؤال 1 · سطر 3", q.Location);
    }

    [Theory]
    [InlineData("ب) الثانية *")]
    [InlineData("ب) الثانية ✓")]
    [InlineData("ب) الثانية (صح)")]
    [InlineData("✓ ب- الثانية")]
    public void OtherMarkers_AreDetected(string markedLine)
    {
        var q = Assert.Single(Read($"1- سؤال؟\nأ) الدقيقة\n{markedLine}\nج) الساعة").Questions);
        Assert.Equal(1, q.CorrectIndex);
        Assert.Equal("الثانية", q.Options[1]);
    }

    [Fact]
    public void AnswerLine_IsDetected()
    {
        var q = Assert.Single(Read("1) سؤال؟\nأ. خيار1\nب. خيار2\nج. خيار3\nالإجابة: ج").Questions);
        Assert.Equal(2, q.CorrectIndex);
        Assert.Equal(AnswerDetectors.AnswerLine, q.Detector);
    }

    [Fact]
    public void AnswerKeyAtEnd_WithArabicDigitsAndSeveralPerLine()
    {
        var r = Read("""
            ١- السؤال الأول؟
            أ) أ1
            ب) ب1
            ٢- السؤال الثاني؟
            أ) أ2
            ب) ب2
            ج) ج2

            مفتاح الإجابات
            ١-ب  ٢-ج
            """);

        Assert.Equal(2, r.Questions.Count);
        Assert.Equal(1, r.Questions[0].CorrectIndex);
        Assert.Equal(2, r.Questions[1].CorrectIndex);
        Assert.All(r.Questions, q => Assert.Equal(AnswerDetectors.AnswerKey, q.Detector));
    }

    [Fact]
    public void AgreeingDetectors_AreRecordedTogether()
    {
        var q = Assert.Single(Read("1. سؤال؟\nأ) س\n*ب) ص\n\nالإجابات:\n1-ب").Questions);
        Assert.Equal(1, q.CorrectIndex);
        Assert.Equal("marker+answer-key", q.Detector);
        Assert.Equal(ExtractionConfidence.High, q.Confidence);
    }

    [Fact]
    public void ConflictingDetectors_LeaveAnswerEmpty_ForReview()
    {
        var q = Assert.Single(Read("1. سؤال؟\nأ) س\n*ب) ص\nج) ع\n\nالإجابات:\n1-ج").Questions);
        Assert.Null(q.CorrectIndex);
        Assert.Equal(AnswerDetectors.Conflict, q.Detector);
        Assert.Equal(ExtractionConfidence.Low, q.Confidence);
        Assert.Contains(q.Issues, i => i.StartsWith("تعارض"));
    }

    [Fact]
    public void NoAnswer_IsLowConfidence()
    {
        var q = Assert.Single(Read("1. سؤال؟\nأ) س\nب) ص").Questions);
        Assert.Null(q.CorrectIndex);
        Assert.Equal(ExtractionConfidence.Low, q.Confidence);
        Assert.Contains("لم يُعثر على علامة للإجابة الصحيحة", q.Issues);
    }

    [Fact]
    public void ContinuationLines_AttachToStemOrLastOption()
    {
        var q = Assert.Single(Read("1. نص السؤال\nيكمل هنا؟\nأ) خيار أول\nيكمل الخيار\n*ب) خيار ثانٍ").Questions);
        Assert.Equal("نص السؤال\nيكمل هنا؟", q.Text);
        Assert.Equal("خيار أول يكمل الخيار", q.Options[0]);
    }

    [Fact]
    public void LatinOptions_AndAlefVariant_AreNormalizedForMatching()
    {
        var r = Read("1. Q one?\na) one\nb) two\nAnswer: b\n\n2. سؤال؟\nا) أول\nب) ثانٍ\nالإجابة: أ");
        Assert.Equal(1, r.Questions[0].CorrectIndex);
        Assert.Equal(0, r.Questions[1].CorrectIndex);
    }

    [Fact]
    public void AnswerPointingToMissingLetter_IsReported()
    {
        var q = Assert.Single(Read("1. سؤال؟\nأ) س\nب) ص\nالإجابة: د").Questions);
        Assert.Null(q.CorrectIndex);
        Assert.Contains(q.Issues, i => i.Contains("«د»"));
    }

    [Fact]
    public void NumberedLineWithoutOptions_IsSkippedWithWarning()
    {
        var r = Read("1. تعليمات: اقرأ الأسئلة جيدًا\n2. سؤال؟\nأ) س\n*ب) ص");
        Assert.Single(r.Questions);
        Assert.Single(r.Warnings);
        Assert.Equal(1, r.Questions[0].Order);
    }

    [Fact]
    public void Windows1256File_IsDecoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1256).GetBytes("1. ما عاصمة الأردن؟\r\nأ) إربد\r\n*ب) عمّان\r\n");
        var q = Assert.Single(new TextQuestionReader().Read(bytes).Questions);
        Assert.Equal("ما عاصمة الأردن؟", q.Text);
        Assert.Equal("عمّان", q.Options[1]);
        Assert.Equal(1, q.CorrectIndex);
    }

    [Fact]
    public void DuplicateOptions_LowerConfidence()
    {
        var q = Assert.Single(Read("1. سؤال؟\nأ) نعم\n*ب) نعم\nج) لا").Questions);
        Assert.Equal(ExtractionConfidence.Medium, q.Confidence);
        Assert.Contains("خيارات مكررة", q.Issues);
    }
}

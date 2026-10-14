using System.Text;
using QuestionBank.Core.Extraction;

namespace QuestionBank.Tests.Extraction;

public sealed class GiftReaderTests
{
    private static ReaderResult Read(string gift) => new GiftReader().Read(Encoding.UTF8.GetBytes(gift));

    [Fact]
    public void MultipleChoice_KeepsTextOptionsOrderAndAnswer()
    {
        var r = Read("""
            // تعليق يُتجاهل
            $CATEGORY: $course$/إداري

            ::Q-001::عند تعارض مهمتين، ما أول إجراء مناسب؟ {
              ~تنفيذ الأسهل أولًا
              =تقييم الأثر ومناقشة الأولوية مع المسؤول#إجابة صحيحة
              ~تأجيل المهمتين
              ~تفويض المهمتين
            }
            """);

        var q = Assert.Single(r.Questions);
        Assert.Equal("Q-001", q.Code);
        Assert.Equal("عند تعارض مهمتين، ما أول إجراء مناسب؟", q.Text);
        Assert.Equal(["تنفيذ الأسهل أولًا", "تقييم الأثر ومناقشة الأولوية مع المسؤول", "تأجيل المهمتين", "تفويض المهمتين"], q.Options);
        Assert.Equal(1, q.CorrectIndex);
        Assert.Equal(ExtractionConfidence.High, q.Confidence);
        Assert.Equal(AnswerDetectors.Gift, q.Detector);
        Assert.Equal("سطر 4", q.Location);
    }

    [Fact]
    public void EscapedCharacters_AreUnescaped_AndNotTreatedAsSyntax()
    {
        var r = Read(@"ما ناتج 2 \= 1 \+ 1 في \{المجموعة\}؟ {=صحيح \~ تقريبًا ~خطأ\: دائمًا ~لا أعرف \# رقم}");

        var q = Assert.Single(r.Questions);
        Assert.Equal(@"ما ناتج 2 = 1 \+ 1 في {المجموعة}؟", q.Text);
        Assert.Equal(["صحيح ~ تقريبًا", "خطأ: دائمًا", "لا أعرف # رقم"], q.Options);
        Assert.Equal(0, q.CorrectIndex);
    }

    [Fact]
    public void PercentWeights_FullWeightIsCorrect_PartialIsFlagged()
    {
        var q = Assert.Single(Read("سؤال؟ {~%100%الصحيح ~%50%نصف ~%0%خطأ}").Questions);
        Assert.Equal(0, q.CorrectIndex);
        Assert.Contains("أوزان جزئية للإجابات", q.Issues);
    }

    [Fact]
    public void TrueFalse_BecomesTwoOptions()
    {
        var r = Read("""
            الأرض تدور حول الشمس. {T}

            القمر نجم. {FALSE#القمر تابع}
            """);
        Assert.Equal(2, r.Questions.Count);
        Assert.Equal(0, r.Questions[0].CorrectIndex);
        Assert.Equal(1, r.Questions[1].CorrectIndex);
        Assert.Equal(["صواب", "خطأ"], r.Questions[1].Options);
    }

    [Fact]
    public void UnsupportedTypes_AreSkippedWithWarnings()
    {
        var r = Read("""
            إجابة قصيرة؟ {=الرياض =الرياض.}

            طابق: {=أ -> 1 =ب -> 2}

            رقمي؟ {#3.14:0.01}

            نص وصفي فقط بلا أقواس
            """);
        Assert.Empty(r.Questions);
        Assert.Equal(4, r.Warnings.Count);
    }

    [Fact]
    public void MultipleCorrect_IsConflict_LowConfidence()
    {
        var q = Assert.Single(Read("سؤال؟ {=أ =ب ~ج}").Questions);
        Assert.Null(q.CorrectIndex);
        Assert.Equal(AnswerDetectors.Conflict, q.Detector);
        Assert.Equal(ExtractionConfidence.Low, q.Confidence);
    }

    [Fact]
    public void MissingWordFormat_KeepsTextAroundTheBlank()
    {
        var q = Assert.Single(Read("عاصمة الأردن {=عمّان ~إربد ~الزرقاء} هي أكبر مدنه.").Questions);
        Assert.Equal("عاصمة الأردن _____ هي أكبر مدنه.", q.Text);
        Assert.Equal(0, q.CorrectIndex);
    }

    [Fact]
    public void RoundTrip_WithPrototypeExporterFormat()
    {
        // نفس الصيغة التي يصدّرها النموذج التفاعلي: تعليق، عنوان، خيارات على أسطر، رموز مهرَّبة.
        var r = Read("""
            // Q-000108 | مصدر: إداري/2024/اختبار.xlsx | صف 5
            ::Q-000108::أي أداة تساعد على ترتيب المهام\: الأهمية والإلحاح؟ {
              ~مخطط جانت
              =مصفوفة أيزنهاور
              ~خريطة ذهنية
              ~قائمة مراجعة
            }
            """);
        var q = Assert.Single(r.Questions);
        Assert.Equal("أي أداة تساعد على ترتيب المهام: الأهمية والإلحاح؟", q.Text);
        Assert.Equal(1, q.CorrectIndex);
        Assert.Equal(4, q.Options.Count);
    }
}

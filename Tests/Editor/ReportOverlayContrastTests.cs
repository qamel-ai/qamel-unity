using NUnit.Framework;
using UnityEngine;

namespace QamelCapture.Tests
{
    public class ReportOverlayContrastTests
    {
        [Test]
        public void TextAreaHasNoThemeBackgroundsInAnyInteractionState()
        {
            AssertReadableStyle(ReportOverlay.CreateTextStyle(), ReportOverlay.Sand);
        }

        [Test]
        public void LabelsAndButtonTextStayReadableInEveryInteractionState()
        {
            AssertReadableStyle(ReportOverlay.LabelStyle(18, ReportOverlay.Ink), Color.white);
            AssertReadableStyle(ReportOverlay.LabelStyle(18, ReportOverlay.MutedInk), Color.white);
            AssertReadableStyle(ReportOverlay.LabelStyle(16, Color.white), ReportOverlay.Ink);
        }

        [Test]
        public void SelectedTextAndCaretContrastWithTheField()
        {
            AssertContrast(ReportOverlay.Ink, ReportOverlay.Selection);
            AssertContrast(ReportOverlay.Ink, ReportOverlay.Sand);
            Assert.AreEqual(1f, ReportOverlay.Selection.a, "Selection must not blend with a host theme.");
        }

        static void AssertReadableStyle(GUIStyle style, Color background)
        {
            foreach (var state in new[] { style.normal, style.hover, style.active, style.focused,
                style.onNormal, style.onHover, style.onActive, style.onFocused })
            {
                Assert.IsNull(state.background);
                Assert.IsTrue(state.scaledBackgrounds == null || state.scaledBackgrounds.Length == 0,
                    "High-DPI Editor backgrounds must not cover Qamel's field surface.");
                Assert.AreEqual(1f, state.textColor.a);
                AssertContrast(state.textColor, background);
            }
        }

        static void AssertContrast(Color foreground, Color background)
        {
            float a = Luminance(foreground), b = Luminance(background);
            float ratio = (Mathf.Max(a, b) + 0.05f) / (Mathf.Min(a, b) + 0.05f);
            Assert.GreaterOrEqual(ratio, 4.5f, "Normal-size text must retain at least 4.5:1 contrast.");
        }

        static float Luminance(Color color) =>
            0.2126f * Linear(color.r) + 0.7152f * Linear(color.g) + 0.0722f * Linear(color.b);

        static float Linear(float value) =>
            value <= 0.04045f ? value / 12.92f : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);
    }
}

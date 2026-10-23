using NUnit.Framework;
using UnityEngine;

namespace QamelCapture.Tests
{
    public class ReportOverlayLayoutTests
    {
        [TestCase(320, 180)]
        [TestCase(480, 270)]
        [TestCase(640, 360)]
        [TestCase(800, 450)]
        [TestCase(960, 540)]
        [TestCase(1280, 720)]
        [TestCase(1920, 1080)]
        [TestCase(3840, 2160)]
        [TestCase(600, 900)]
        [TestCase(2560, 720)]
        public void FormAndButtonsStayInsideTheWindow(int width, int height)
        {
            var layout = ReportOverlay.CalculateLayout(width, height);
            var window = new Rect(0, 0, width / layout.Scale, height / layout.Scale);
            AssertContains(window, layout.Panel);
            foreach (var rect in new[] { layout.Title, layout.Description, layout.FieldLabel,
                layout.Counter, layout.Text, layout.Cancel, layout.Submit })
                AssertContains(layout.Panel, rect);

            Assert.GreaterOrEqual(layout.Text.height, 80f, "A small window still needs room to type.");
            Assert.LessOrEqual(layout.Description.yMax, layout.FieldLabel.y);
            Assert.LessOrEqual(layout.FieldLabel.yMax, layout.Text.y);
            Assert.LessOrEqual(layout.Text.yMax + 16f, layout.Cancel.y);
            Assert.Less(layout.Cancel.xMax, layout.Submit.x);
            Assert.GreaterOrEqual(layout.Panel.yMax - layout.Submit.yMax, 24f);
        }

        [TestCase(640, 360)]
        [TestCase(800, 450)]
        [TestCase(960, 540)]
        public void SmallGameWindowsKeepFullSizeText(int width, int height)
        {
            var layout = ReportOverlay.CalculateLayout(width, height);
            Assert.AreEqual(1f, layout.Scale);
            Assert.GreaterOrEqual(layout.Panel.width, 608f);
            Assert.GreaterOrEqual(layout.Cancel.height, 60f);
        }

        static void AssertContains(Rect outer, Rect inner)
        {
            Assert.Greater(inner.width, 0f);
            Assert.Greater(inner.height, 0f);
            Assert.GreaterOrEqual(inner.xMin, outer.xMin);
            Assert.GreaterOrEqual(inner.yMin, outer.yMin);
            Assert.LessOrEqual(inner.xMax, outer.xMax);
            Assert.LessOrEqual(inner.yMax, outer.yMax);
        }
    }
}

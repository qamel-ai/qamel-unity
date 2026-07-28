using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    public class ReportTextEditorTests
    {
        static ReportTextEditor Typed(string text, int maxLength = 500)
        {
            var editor = new ReportTextEditor(maxLength);
            editor.Insert(text);
            return editor;
        }

        [Test]
        public void InsertsAtTheCaretRatherThanTheEnd()
        {
            var editor = Typed("abcd");
            editor.MoveLeft();
            editor.MoveLeft();
            editor.Insert('X');
            Assert.AreEqual("abXcd", editor.Text);
            Assert.AreEqual(3, editor.Caret);
        }

        /// <summary>
        /// The bug this class exists for: text that looked deleted came back with
        /// the next keystroke, appended at the end.
        /// </summary>
        [Test]
        public void DeletedTextStaysDeletedWhenTypingResumes()
        {
            var editor = Typed("hello");
            editor.Backspace();
            editor.Backspace();
            Assert.AreEqual("hel", editor.Text);

            editor.Insert('p');
            Assert.AreEqual("help", editor.Text);
        }

        [Test]
        public void BackspaceRemovesBeforeTheCaretAndDeleteAfterIt()
        {
            var editor = Typed("abcd");
            editor.MoveLeft(); // between c and d
            Assert.IsTrue(editor.Backspace());
            Assert.AreEqual("abd", editor.Text);
            Assert.AreEqual(2, editor.Caret);

            Assert.IsTrue(editor.Delete());
            Assert.AreEqual("ab", editor.Text);
            Assert.AreEqual(2, editor.Caret);
        }

        [Test]
        public void EditingAtTheEdgesIsANoOp()
        {
            var editor = Typed("ab");
            editor.MoveHome();
            Assert.IsFalse(editor.Backspace());
            editor.MoveEnd();
            Assert.IsFalse(editor.Delete());
            Assert.AreEqual("ab", editor.Text);
        }

        [Test]
        public void CaretMovementIsClampedToTheText()
        {
            var editor = Typed("ab");
            editor.MoveRight();
            editor.MoveRight();
            Assert.AreEqual(2, editor.Caret);
            editor.MoveLeft();
            editor.MoveLeft();
            editor.MoveLeft();
            Assert.AreEqual(0, editor.Caret);
        }

        [Test]
        public void HomeAndEndActOnTheCurrentLine()
        {
            var editor = new ReportTextEditor(500);
            editor.Insert("one");
            editor.NewLine();
            editor.Insert("two");

            editor.MoveHome();
            Assert.AreEqual(4, editor.Caret); // start of "two"
            editor.MoveEnd();
            Assert.AreEqual(7, editor.Caret);

            editor.MoveLeft();
            editor.MoveLeft();
            editor.MoveLeft();
            editor.MoveLeft(); // onto the newline boundary
            editor.MoveHome();
            Assert.AreEqual(0, editor.Caret);
        }

        [Test]
        public void StopsAcceptingInputAtMaxLengthButStillEdits()
        {
            var editor = new ReportTextEditor(3);
            Assert.IsTrue(editor.Insert('a'));
            Assert.IsTrue(editor.Insert('b'));
            Assert.IsTrue(editor.Insert('c'));
            Assert.IsFalse(editor.Insert('d'));
            Assert.IsTrue(editor.IsFull);
            Assert.AreEqual("abc", editor.Text);

            Assert.IsTrue(editor.Backspace());
            Assert.IsFalse(editor.IsFull);
            Assert.IsTrue(editor.Insert('z'));
            Assert.AreEqual("abz", editor.Text);
        }

        [Test]
        public void InsertingAStringStopsAtMaxLength()
        {
            var editor = new ReportTextEditor(4);
            Assert.IsTrue(editor.Insert("abcdef"));
            Assert.AreEqual("abcd", editor.Text);
            Assert.AreEqual(4, editor.Caret);
        }

        [Test]
        public void RendersTheCaretWhereTheNextCharacterLands()
        {
            var editor = Typed("abc");
            Assert.AreEqual("abc|", editor.Render("|"));
            editor.MoveLeft();
            Assert.AreEqual("ab|c", editor.Render("|"));
            editor.MoveHome();
            Assert.AreEqual("|abc", editor.Render("|"));
            Assert.AreEqual("abc", editor.Render(null));
        }

        [Test]
        public void SetReplacesTheBufferAndParksTheCaretAtTheEnd()
        {
            var editor = Typed("abc");
            editor.MoveHome();
            editor.Set("replaced");
            Assert.AreEqual("replaced", editor.Text);
            Assert.AreEqual(8, editor.Caret);

            editor.Set(null);
            Assert.AreEqual("", editor.Text);
            Assert.AreEqual(0, editor.Caret);
        }

        [Test]
        public void SetTruncatesOverlongTextToMaxLength()
        {
            var editor = new ReportTextEditor(4);
            editor.Set("abcdefgh");
            Assert.AreEqual("abcd", editor.Text);
            Assert.AreEqual(4, editor.Caret);
        }

        [Test]
        public void ClearResetsTextAndCaret()
        {
            var editor = Typed("abc");
            editor.Clear();
            Assert.AreEqual("", editor.Text);
            Assert.AreEqual(0, editor.Caret);
            Assert.AreEqual(0, editor.Length);
        }
    }
}

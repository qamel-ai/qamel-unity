using System.Text;

namespace QamelCapture
{
    /// <summary>
    /// Caret-aware text buffer for the report form on input backends where IMGUI
    /// receives no events and Qamel has to assemble the text itself.
    ///
    /// It exists because the alternative -- letting an editable IMGUI text area
    /// draw a string that Qamel keeps separately -- gives the field two owners:
    /// the control's internal editor accepts edits that never reach the string,
    /// so deletions appear to work and then come back with the next keystroke.
    /// Here the buffer is the only owner and the overlay draws what it reports,
    /// caret included. No UnityEngine dependency, so it is directly testable.
    /// </summary>
    internal sealed class ReportTextEditor
    {
        readonly int _maxLength;
        readonly StringBuilder _text = new StringBuilder();
        int _caret;

        public ReportTextEditor(int maxLength)
        {
            _maxLength = maxLength < 1 ? 1 : maxLength;
        }

        public string Text => _text.ToString();

        public int Length => _text.Length;

        /// <summary>Insertion point, between 0 and <see cref="Length"/>.</summary>
        public int Caret => _caret;

        public bool IsFull => _text.Length >= _maxLength;

        public void Clear()
        {
            _text.Length = 0;
            _caret = 0;
        }

        /// <summary>
        /// Replaces the whole buffer, e.g. when the interactive IMGUI form hands
        /// its edited string back. The caret follows to the end of the text.
        /// </summary>
        public void Set(string text)
        {
            _text.Length = 0;
            if (!string.IsNullOrEmpty(text))
            {
                _text.Append(text.Length > _maxLength ? text.Substring(0, _maxLength) : text);
            }
            _caret = _text.Length;
        }

        public bool Insert(char c)
        {
            if (IsFull) return false;
            _text.Insert(_caret, c);
            _caret++;
            return true;
        }

        public bool Insert(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            bool inserted = false;
            for (int i = 0; i < value.Length; i++)
            {
                if (!Insert(value[i])) break;
                inserted = true;
            }
            return inserted;
        }

        public bool NewLine()
        {
            return Insert('\n');
        }

        /// <summary>Deletes the character before the caret.</summary>
        public bool Backspace()
        {
            if (_caret <= 0) return false;
            _text.Remove(_caret - 1, 1);
            _caret--;
            return true;
        }

        /// <summary>Deletes the character after the caret.</summary>
        public bool Delete()
        {
            if (_caret >= _text.Length) return false;
            _text.Remove(_caret, 1);
            return true;
        }

        public void MoveLeft()
        {
            if (_caret > 0) _caret--;
        }

        public void MoveRight()
        {
            if (_caret < _text.Length) _caret++;
        }

        /// <summary>Start of the current line, which is what Home means in a text area.</summary>
        public void MoveHome()
        {
            while (_caret > 0 && _text[_caret - 1] != '\n') _caret--;
        }

        public void MoveEnd()
        {
            while (_caret < _text.Length && _text[_caret] != '\n') _caret++;
        }

        /// <summary>
        /// The text as it should be drawn, with <paramref name="caretGlyph"/>
        /// inserted at the caret. Pass null to draw the text alone (a blinking
        /// caret simply alternates between the two).
        /// </summary>
        public string Render(string caretGlyph)
        {
            if (string.IsNullOrEmpty(caretGlyph)) return Text;
            return _text.ToString(0, _caret) + caretGlyph +
                   _text.ToString(_caret, _text.Length - _caret);
        }
    }
}

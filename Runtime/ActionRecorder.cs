using System;
using System.Collections.Generic;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace QamelCapture
{
    /// <summary>Observes game-owned actions without changing their configuration.</summary>
    internal sealed class ActionRecorder : IDisposable
    {
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        readonly ISessionSink _sink;
        readonly Func<double> _now;
        readonly Func<bool> _allowed;
        readonly Dictionary<InputAction, double> _last = new Dictionary<InputAction, double>();
#endif
        public ActionRecorder(ISessionSink sink, Func<double> now, Func<bool> allowed)
        {
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            _sink = sink; _now = now; _allowed = allowed;
            InputSystem.onActionChange += OnActionChange;
#endif
        }
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        void OnActionChange(object value, InputActionChange change)
        {
            if (!(value is InputAction action)) return;
            if (change == InputActionChange.ActionDisabled) { _last.Remove(action); return; }
            if (!_allowed()) return;
            string phase;
            if (change == InputActionChange.ActionStarted) phase = "started";
            else if (change == InputActionChange.ActionPerformed) phase = "performed";
            else if (change == InputActionChange.ActionCanceled) phase = "canceled";
            else return;
            double t = _now();
            if (phase == "performed" && action.type != InputActionType.Button &&
                _last.TryGetValue(action, out double previous) && t - previous < 0.05) return;
            if (phase == "performed") _last[action] = t;
            if (phase == "canceled") _last.Remove(action);
            if (_last.Count > 4096) _last.Clear();
            _sink.AddEvent(t, SessionEvents.InputAction(t, action.id.ToString(), action.name,
                action.actionMap?.name ?? "", phase, action.activeControl?.path ?? ""));
        }
#endif
        public void Dispose()
        {
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            InputSystem.onActionChange -= OnActionChange;
            _last.Clear();
#endif
        }
    }
}

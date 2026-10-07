using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace QamelCapture.Tests
{
    public class ReviewCaptureRuntimeTests
    {
        [Test]
        public void ActionCaptureObservesBindingsWithoutEnablingActions()
        {
            var sink = new SessionBuffer(30,1);
            var keyboard = InputSystem.AddDevice<Keyboard>();
            try
            {
                using (var map = new InputActionMap("Gameplay"))
                using (var recorder = new ActionRecorder(sink,()=>1,()=>true))
                {
                    var action = map.AddAction("Jump",InputActionType.Button,"<Keyboard>/space");
                    Assert.IsFalse(action.enabled);
                    action.Enable();
                    InputState.Change(keyboard,new KeyboardState(Key.Space),InputUpdateType.Dynamic);
                    InputState.Change(keyboard,new KeyboardState(),InputUpdateType.Dynamic);
                    var events = new List<string>(); sink.Snapshot(events,new List<CapturedFrame>());
                    Assert.IsTrue(events.Any(e => e.Contains("\"action_name\":\"Jump\"") && e.Contains("\"phase\":\"performed\"")),string.Join("\n",events));
                    Assert.IsTrue(events.Any(e => e.Contains("\"phase\":\"canceled\"")));
                }
            }
            finally { InputSystem.RemoveDevice(keyboard); }
        }
    }
}

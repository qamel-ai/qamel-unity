using System;
using System.Collections.Generic;
using UnityEngine.LowLevel;

namespace QamelCapture.TestAuthoring
{
    // Set the next simulation step only after gameplay has consumed this one.
    // The final input frame must also reach Update/LateUpdate before cleanup.
    internal sealed class InputReplayPlayerLoop : IDisposable
    {
        sealed class AfterGameplay { }
        static InputReplayPlayerLoop _owner;
        readonly Action _afterGameplay;

        public InputReplayPlayerLoop(Action afterGameplay)
        {
            _afterGameplay = afterGameplay;
            if (_owner != null)
                throw new InvalidOperationException("Only one input replay can control simulation timing.");
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var systems = new List<PlayerLoopSystem>(loop.subSystemList);
            systems.Add(new PlayerLoopSystem { type = typeof(AfterGameplay), updateDelegate = () => afterGameplay() });
            loop.subSystemList = systems.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
            _owner = this;
        }

        // InputTestFixture drives input and gameplay manually without a player loop.
        internal static void FinishCurrentFrame() => _owner?._afterGameplay();

        public void Dispose()
        {
            if (_owner != this)
                return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var systems = new List<PlayerLoopSystem>(loop.subSystemList);
            systems.RemoveAll(system => system.type == typeof(AfterGameplay));
            loop.subSystemList = systems.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
            _owner = null;
        }
    }
}

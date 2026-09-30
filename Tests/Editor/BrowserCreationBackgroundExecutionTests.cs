using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using UnityEngine;

namespace QamelCapture.Tests
{
    public sealed class BrowserCreationBackgroundExecutionTests
    {
        [Test]
        public void KeepsBrowserCreationRunningAndRestoresThePreviousSetting()
        {
            bool original = Application.runInBackground;
            try
            {
                BrowserCreationBackgroundExecution.Release();
                Application.runInBackground = false;

                BrowserCreationBackgroundExecution.Hold();
                Assert.IsTrue(Application.runInBackground);

                BrowserCreationBackgroundExecution.Hold();
                BrowserCreationBackgroundExecution.Release();
                Assert.IsFalse(Application.runInBackground);

                Application.runInBackground = true;
                BrowserCreationBackgroundExecution.Hold();
                BrowserCreationBackgroundExecution.Release();
                Assert.IsTrue(Application.runInBackground);
            }
            finally
            {
                BrowserCreationBackgroundExecution.Release();
                Application.runInBackground = original;
            }
        }
    }
}

namespace WowDualBoxAssist;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Two copies would both swap on one press and cancel each other out, so only one runs.
        // A restart as administrator waits a moment for the old copy to close.
        using var mutex = new Mutex(false, @"Local\WowDualBoxAssist");
        bool owned;
        try { owned = mutex.WaitOne(args.Contains(MainForm.RestartArg) ? 5000 : 0); }
        catch (AbandonedMutexException) { owned = true; }

        if (!owned)
        {
            var existing = Native.FindWindow(null, MainForm.WindowTitle);
            if (existing != IntPtr.Zero) WindowActivator.BringToFront(existing);
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}

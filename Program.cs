namespace BluestacksCfgEditor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.ThreadException += (_, e) => ErrorLogger.ShowUnexpectedError(null, "BlueStacks CFG Editor Error", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Exception exception = e.ExceptionObject as Exception
                    ?? new InvalidOperationException("A non-exception unhandled error occurred.");
                ErrorLogger.LogException(exception);
            };

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                ErrorLogger.ShowUnexpectedError(null, "BlueStacks CFG Editor Error", ex);
            }
        }
    }
}

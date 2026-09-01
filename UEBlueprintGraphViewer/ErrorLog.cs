using System;
using System.IO;
using System.Text;

namespace UEBlueprintGraphViewer
{
    public static class ErrorLog
    {
        public const string FileName = "error.log";

        public static string LastError { get; private set; } = "";

        public static string FilePath =>
            Path.Combine(AppContext.BaseDirectory, FileName);

        public static string Format(Exception ex) => ex.ToString();

        public static void Record(Exception ex, string? context = null)
        {
            var text = new StringBuilder();
            text.AppendLine("==========");
            text.AppendLine(DateTime.Now.ToString("O"));
            if (!string.IsNullOrEmpty(context))
                text.AppendLine(context);
            text.AppendLine(ex.ToString());
            if (ex.InnerException != null && !ex.ToString().Contains(ex.InnerException.ToString(), StringComparison.Ordinal))
            {
                text.AppendLine("--- Inner ---");
                text.AppendLine(ex.InnerException.ToString());
            }

            LastError = text.ToString();
            try
            {
                File.AppendAllText(FilePath, LastError + Environment.NewLine);
            }
            catch
            {
                // logging must never take the app down
            }
        }

        public static string DialogText(Exception ex, string? context = null)
        {
            Record(ex, context);
            return $"{ex.GetType().FullName}: {ex.Message}\n\n{ex.StackTrace}\n\nLogged to {FilePath}";
        }
    }
}

using System;

namespace Dasher.Windows.Services;

public static class ToastNotifier
{
    public static void Show(string title, string message, bool isWarning = false)
    {
        Show(title, message, null, isWarning);
    }

    /// <summary>
    /// Show a toast with an optional launch URL — tapping the toast opens it
    /// (RFC 0017 passive update notification).
    /// </summary>
    public static void Show(string title, string message, string? launchUrl, bool isWarning = false)
    {
        try
        {
            var launchAttr = string.IsNullOrEmpty(launchUrl)
                ? ""
                : $" launch=\"{System.Security.SecurityElement.Escape(launchUrl)}\" activationType=\"protocol\"";
            var xml = $@"<toast{launchAttr}>
                <visual>
                    <binding template=""ToastGeneric"">
                        <text>{System.Security.SecurityElement.Escape(title)}</text>
                        <text>{System.Security.SecurityElement.Escape(message)}</text>
                    </binding>
                </visual>
            </toast>";

            var doc = new global::Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);

            var toast = new global::Windows.UI.Notifications.ToastNotification(doc);
            global::Windows.UI.Notifications.ToastNotificationManager
                .CreateToastNotifier("Dasher")
                .Show(toast);
        }
        catch
        {
        }
    }
}

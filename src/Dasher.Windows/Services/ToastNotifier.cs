using System;

namespace Dasher.Windows.Services;

public static class ToastNotifier
{
    public static void Show(string title, string message, bool isWarning = false)
    {
        try
        {
            var xml = $@"<toast>
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

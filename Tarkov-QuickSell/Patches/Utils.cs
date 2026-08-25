using EFT.Communications;

namespace QuickSell.Patches
{
    public class Utils
    {
        public static void SendNotification(string text)
        {
            NotificationManager.DisplayMessageNotification(text, ENotificationDurationType.Long);
        }

        public static void SendError(string text)
        {
            NotificationManager.DisplayWarningNotification(text, ENotificationDurationType.Long);
        }
    }
}

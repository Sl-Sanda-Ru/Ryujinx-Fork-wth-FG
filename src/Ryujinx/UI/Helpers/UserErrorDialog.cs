using Ryujinx.Ava.Common.Locale;
using Ryujinx.Common.UI;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Helpers
{
    internal class UserErrorDialog
    {
        private static string GetErrorCode(UserError error)
        {
            return $"RYU-{(uint)error:X4}";
        }

        private static string GetErrorTitle(UserError error) =>
            error switch
            {
                UserError.NoKeys => LocaleManager.Instance[LocaleKeys.Error_NoKeysFound],
                UserError.NoFirmware => LocaleManager.Instance[LocaleKeys.Error_NoFirmwareFound],
                UserError.FirmwareParsingFailed => LocaleManager.Instance[LocaleKeys.Error_FirmwareParsingFailed],
                UserError.ApplicationNotFound => LocaleManager.Instance[LocaleKeys.UserErrorApplicationNotFound],
                UserError.Unknown => LocaleManager.Instance[LocaleKeys.UserErrorUnknown],
                _ => LocaleManager.Instance[LocaleKeys.UserErrorUndefined],
            };

        private static string GetErrorDescription(UserError error) =>
            error switch
            {
                UserError.NoKeys => LocaleManager.Instance[LocaleKeys.Error_NoKeysFoundDescription],
                UserError.NoFirmware => LocaleManager.Instance[LocaleKeys.Error_NoFirmwareFoundDescription],
                UserError.FirmwareParsingFailed => LocaleManager.Instance[LocaleKeys.Error_FirmwareParsingFailedDescription],
                UserError.ApplicationNotFound => LocaleManager.Instance[LocaleKeys.UserErrorApplicationNotFoundDescription],
                UserError.Unknown => LocaleManager.Instance[LocaleKeys.UserErrorUnknownDescription],
                _ => LocaleManager.Instance[LocaleKeys.UserErrorUndefinedDescription],
            };

        /// <summary>
        /// Shows the dialog for a user error, optionally with the underlying detail appended.
        /// </summary>
        /// <param name="details">
        /// Extra context from the exception behind the error, such as the name of the key that was
        /// missing. The generic descriptions cannot distinguish an absent keys file from one that
        /// loaded but lacks the generation a particular title needs, and those two call for
        /// completely different fixes, so the detail is worth showing when there is one.
        /// </param>
        public static async Task ShowUserErrorDialog(UserError error, string details = null)
        {
            string errorCode = GetErrorCode(error);

            string description = GetErrorDescription(error);

            if (!string.IsNullOrWhiteSpace(details))
            {
                description += $"\n\n{details.Trim()}";
            }

            await ContentDialogHelper.CreateInfoDialog(
                LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.DialogUserErrorDialogMessage, errorCode, GetErrorTitle(error)),
                description,
                string.Empty,
                LocaleManager.Instance[LocaleKeys.InputDialogOk],
                LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.DialogUserErrorDialogTitle, errorCode));
        }
    }
}

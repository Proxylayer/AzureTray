namespace AzureTray.Plugin.LAPS;

// One row from /directory/deviceLocalCredentials. DirectoryRecordId is the
// id used for the password fetch — NOT the Entra device id.
internal sealed record LapsDevice(string DirectoryRecordId, string DisplayName);

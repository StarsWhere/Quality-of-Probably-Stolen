namespace ShopAssist
{
    internal enum ActionUnavailableReason { None, Disabled, MissingSelection, MissingObject, MissingResource, Unsupported, Conflict, Unknown }

    internal readonly struct ActionAvailability
    {
        internal ActionUnavailableReason Code { get; }
        internal string ReasonKey { get; }
        internal string SettingKey { get; }
        internal bool Allowed => Code == ActionUnavailableReason.None;
        internal string Message => Allowed ? "" : ShopSettings.Text(ReasonKey ?? "availability.unknown");
        private ActionAvailability(ActionUnavailableReason code, string reasonKey, string settingKey)
        { Code = code; ReasonKey = reasonKey; SettingKey = settingKey; }
        internal static ActionAvailability Ready => default;
        internal static ActionAvailability Blocked(ActionUnavailableReason code, string reasonKey, string settingKey = null) => new ActionAvailability(code, reasonKey, settingKey);
    }
}

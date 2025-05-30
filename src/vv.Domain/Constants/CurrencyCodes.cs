namespace vv.Domain.Constants
{
    /// <summary>
    /// Standardized currency code constants (ISO 4217).
    /// </summary>
    public static class CurrencyCodes
    {
        public const string USD = "USD";
        public const string EUR = "EUR";
        public const string GBP = "GBP";
        public const string ZAR = "ZAR";
        public const string JPY = "JPY";
        public const string CHF = "CHF";
        public const string AUD = "AUD";
        public const string CAD = "CAD";
        public const string CNY = "CNY";
        public const string BTC = "BTC";
        public const string ETH = "ETH";
        // Add established currencies as needed. Avoid adding volatile tokens or meme coins as they lack stability for financial applications.

        private static readonly string[] _allCodes =
        {
            USD, EUR, GBP, ZAR, JPY, CHF, AUD, CAD, CNY, BTC, ETH
        };

        /// <summary>
        /// Returns a list of all defined codes.
        /// </summary>
        public static IReadOnlyList<string> All => _allCodes;
    }
}

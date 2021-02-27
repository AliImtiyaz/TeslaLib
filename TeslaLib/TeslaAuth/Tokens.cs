// Code from https://github.com/briangru/TeslaAuth, with work from bassmaster187, Tom Hollander, and briangru
using System;

namespace TeslaAuth
{

    public class Tokens
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime CreatedAt { get; set; }
        public TimeSpan ExpiresIn { get; set; }
    }
}
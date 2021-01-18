using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Bot.AdaptiveCards
{
    public class AdaptiveCardAuthentication
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("connectionName")]
        public string ConnectionName { get; set; }

        [JsonProperty("token")]
        public string Token { get; set; }
    }
}

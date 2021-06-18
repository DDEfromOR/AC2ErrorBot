// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using AdaptiveCards;
using Catering.Cards;
using Catering.Models;
using Microsoft.Bot.AdaptiveCards;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Catering
{
    // This bot will respond to the user's input with an Adaptive Card.
    // Adaptive Cards are a way for developers to exchange card content
    // in a common and consistent way. A simple open card format enables
    // an ecosystem of shared tooling, seamless integration between apps,
    // and native cross-platform performance on any device.
    // For each user interaction, an instance of this class is created and the OnTurnAsync method is called.
    // This is a Transient lifetime service. Transient lifetime services are created
    // each time they're requested. For each Activity received, a new instance of this
    // class is created. Objects that are expensive to construct, or have a lifetime
    // beyond the single turn, should be carefully managed.

    public class CateringBot : ActivityHandler
    {
        private const string WelcomeText = "Welcome. This bot will introduce you to Action.Execute in Adaptive Cards.";
        private BotState _userState;
        private readonly AdaptiveCardOAuthHandler _botAppHandler = new AdaptiveCardOAuthHandler("BotApp", "Sign-In To Bot App", "Sign-In");
        private readonly AdaptiveCardOAuthHandler _nonSsoHandler = new AdaptiveCardOAuthHandler("NonSSOBotApp", "Sign-In To Bot App", "Sign-In");
        private readonly IConfiguration _configuration;

        public CateringBot(IConfiguration configuration, UserState userState)
        {
            _configuration = configuration;
            _userState = userState;

            var oauthCredential = new MicrosoftAppCredentials(
                _configuration.GetSection("MicrosoftAppId")?.Value,
                _configuration.GetSection("MicrosoftAppPassword")?.Value);
            _botAppHandler = new AdaptiveCardOAuthHandler("BotApp", "Sign-In To Bot App", "Sign-In", oauthCredential);
            _nonSsoHandler = new AdaptiveCardOAuthHandler("NonSSOBotApp", "Sign-In To Bot App", "Sign-In", oauthCredential);
        }

        public override async Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken = default(CancellationToken))
        {
            await base.OnTurnAsync(turnContext, cancellationToken);

            await _userState.SaveChangesAsync(turnContext, false, cancellationToken);
        }

        protected override async Task OnMembersAddedAsync(IList<ChannelAccount> membersAdded, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            if (turnContext.Activity.ChannelId == "directline" || turnContext.Activity.ChannelId == "webchat")
            {
                await SendWelcomeMessageAsync(turnContext, cancellationToken);
            }
        }
        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            await turnContext.SendActivityAsync(MessageFactory.Attachment((new CardResource("ErrorOptions.json").AsAttachment())));
        }

        protected override async Task<InvokeResponse> OnInvokeActivityAsync(ITurnContext<IInvokeActivity> turnContext, CancellationToken cancellationToken)
        {
            var oauthCommandsProperty = _userState.CreateProperty<OAuthCommands>(nameof(OAuthCommands));
            var oauthCommands = await oauthCommandsProperty.GetAsync(turnContext, () => new OAuthCommands() { StartedNominalAuth = false, StartedSSOAuth = false });

            if (AdaptiveCardOAuthHandler.IsOAuthInvoke(turnContext))
            {
                if(oauthCommands.StartedNominalAuth)
                {
                    oauthCommands.StartedNominalAuth = false;
                    await oauthCommandsProperty.SetAsync(turnContext, oauthCommands);

                    var result = await _nonSsoHandler.GetUserTokenAsync(turnContext, _userState);

                    if (result.InvokeResponse != null)
                    {
                        return CreateInvokeResponse(HttpStatusCode.OK, result.InvokeResponse);
                    }
                    else
                    {
                        return CreateInvokeResponse(HttpStatusCode.OK, new AdaptiveCardInvokeResponse()
                        {
                            StatusCode = 200,
                            Type = AdaptiveCardsConstants.Message,
                            Value = $"Received a token right away for sso oauth: ${result.TokenResponse.Token}"
                        });
                    }
                }
                else if (oauthCommands.StartedSSOAuth)
                {
                    oauthCommands.StartedSSOAuth = false;
                    await oauthCommandsProperty.SetAsync(turnContext, oauthCommands);

                    var result = await _botAppHandler.GetUserTokenAsync(turnContext, _userState);

                    if (result.InvokeResponse != null)
                    {
                        return CreateInvokeResponse(HttpStatusCode.OK, result.InvokeResponse);
                    }
                    else
                    {
                        return CreateInvokeResponse(HttpStatusCode.OK, new AdaptiveCardInvokeResponse()
                        {
                            StatusCode = 200,
                            Type = AdaptiveCardsConstants.Message,
                            Value = $"Received a token right away for sso oauth: ${result.TokenResponse.Token}"
                        });
                    }
                }
                else
                {
                    return CreateInvokeResponse(HttpStatusCode.BadRequest, new Error("400", $"Received an invoke with name ${turnContext.Activity.Name} but not as a result of a loginRequest"));
                }
            }
            else
            {
                return await base.OnInvokeActivityAsync(turnContext, cancellationToken);
            }
        }

        protected override async Task<AdaptiveCardInvokeResponse> OnAdaptiveCardInvokeAsync(ITurnContext<IInvokeActivity> turnContext, AdaptiveCardInvokeValue invokeValue, CancellationToken cancellationToken)
        {
            var userSA = _userState.CreateProperty<User>(nameof(User));
            var user = await userSA.GetAsync(turnContext, () => new User() { Id = turnContext.Activity.From.Id });

            var oauthCommandsProperty = _userState.CreateProperty<OAuthCommands>(nameof(OAuthCommands));
            var oauthCommands = await oauthCommandsProperty.GetAsync(turnContext, () => new OAuthCommands() { StartedNominalAuth = false, StartedSSOAuth = false });

            try
            {
                if (invokeValue.Action?.Data == null)
                    throw new ArgumentNullException("invokeValue.Action.Data");

                var cardOptions = ((JObject)invokeValue.Action.Data).ToObject<CardOptions>();

                if (invokeValue.Action.Verb == "next")
                {
                    // show another card
                    return CardResponse("BlandCard.json");
                }
                else if(invokeValue.Action.Verb == "back")
                {
                    // show another card
                    return CardResponse("ErrorOptions.json");
                }
                else if (invokeValue.Action.Verb == "err")
                {
                    return await ProcessErrAction(user, cardOptions);
                }
                else if (invokeValue.Action.Verb == "nominal-oauth")
                {
                    oauthCommands.StartedNominalAuth = true;
                    await oauthCommandsProperty.SetAsync(turnContext, oauthCommands);

                    return await ProcessNominalOAuth(turnContext);
                }
                else if (invokeValue.Action.Verb == "sso-oauth")
                {
                    if (invokeValue.Authentication != null && !string.IsNullOrEmpty(invokeValue.Authentication.Token))
                    {
                        // already started the oauth flow
                        oauthCommands.StartedSSOAuth = false;
                        await oauthCommandsProperty.SetAsync(turnContext, oauthCommands);

                        var result = await _botAppHandler.GetUserTokenAsync(turnContext, _userState);

                        return new AdaptiveCardInvokeResponse()
                        {
                            StatusCode = 200,
                            Type = AdaptiveCardsConstants.Message,
                            Value = $"Completed SSO token exchange and now have a user token: ${result.TokenResponse.Token}"
                        };
                    }
                    else
                    {
                        oauthCommands.StartedSSOAuth = true;
                        await oauthCommandsProperty.SetAsync(turnContext, oauthCommands);

                        return await ProcessSSOOAuth(turnContext);
                    }
                }
                else if (invokeValue.Action.Verb == "signout")
                {
                    return await ProcessSignout(turnContext);
                }
                else
                {
                    AdaptiveCardActionException.VerbNotSupported(invokeValue.Action.Type);
                }
            }
            catch (AdaptiveCardActionException e)
            {
                return e.Response;
            }

            throw new InvokeResponseException(HttpStatusCode.NotImplemented);
        }

        private async Task<AdaptiveCardInvokeResponse> ProcessErrAction(User user, CardOptions cardOptions)
        {

            AdaptiveCardInvokeResponse responseBody = null;
            switch ((Card)cardOptions.nextCardToSend)
            {
                case Card.OkWithString:
                    responseBody = OkWithMessageResponse();
                    break;
                case Card.OkWithCard:
                    responseBody = OkWithCardResponse();
                    break;
                case Card.ThrottleWarning:
                    responseBody = ThrottleWarningResponse();
                    break;
                case Card.Teapot:
                    responseBody = TeapotResponse();
                    break;
                case Card.Error:
                    responseBody = BotErrorResponse();
                    break;
                default:
                    throw new NotImplementedException("No card matches that nextCardToSend.");
            }

            return responseBody;
        }

        private async Task<AdaptiveCardInvokeResponse> ProcessNominalOAuth(ITurnContext turnContext)
        {
            var result = await _nonSsoHandler.GetUserTokenAsync(turnContext, _userState);

            if (result.InvokeResponse != null)
            {
                return result.InvokeResponse;
            }
            else
            {
                return new AdaptiveCardInvokeResponse() { 
                    StatusCode = 200,
                    Type = AdaptiveCardsConstants.Message, 
                    Value = $"Received a token right away for nominal oauth: ${result.TokenResponse.Token}" };
            }
        }

        private async Task<AdaptiveCardInvokeResponse> ProcessSSOOAuth(ITurnContext turnContext)
        {
            var result = await _botAppHandler.GetUserTokenAsync(turnContext, _userState);

            if (result.InvokeResponse != null)
            {
                return result.InvokeResponse;
            }
            else
            {
                return new AdaptiveCardInvokeResponse()
                {
                    StatusCode = 200,
                    Type = AdaptiveCardsConstants.Message,
                    Value = $"Received a token right away for sso oauth: ${result.TokenResponse.Token}"
                };
            }
        }

        private async Task<AdaptiveCardInvokeResponse> ProcessSignout(ITurnContext turnContext)
        {
            var result = await _botAppHandler.SignoutAsync(turnContext, _userState);
            result = await _nonSsoHandler.SignoutAsync(turnContext, _userState);

            return result.InvokeResponse;
        }

        private static InvokeResponse CreateInvokeResponse(HttpStatusCode statusCode, object body = null)
        {
            return new InvokeResponse()
            {
                Status = (int)statusCode,
                Body = body
            };
        }

        private static async Task SendWelcomeMessageAsync(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            foreach (var member in turnContext.Activity.MembersAdded)
            {
                if (member.Id != turnContext.Activity.Recipient.Id)
                {
                    var message = MessageFactory.Text(WelcomeText);
                    await turnContext.SendActivityAsync(message, cancellationToken: cancellationToken);
                    await turnContext.SendActivityAsync($"Type anything to see a card here, or type recents to see recent orders.");
                }
            }
        }

        #region Cards As InvokeResponses

        private AdaptiveCardInvokeResponse CardResponse(string cardFileName)
        {
            return new AdaptiveCardInvokeResponse()
            {
                StatusCode = 200,
                Type = AdaptiveCard.ContentType,
                Value = new CardResource(cardFileName).AsJObject()
            };
        }


        #endregion
    

    #region error responses
 /*
 
 ### Errors Returned by the Bot ###
 statusCode Type typeof(value)

200 |  application/vnd.microsoft.activity.message <string>

200 |  application/vnd.microsoft.card.adaptive <AdaptiveCard>

401 |  application/vnd.microsoft.activity.loginRequest <LoginRequest> {“loginUrl”: <string>}

429 |  application/vnd.microsoft.activity.retryAfter <number>

4xx |  application/vnd.microsoft.error <Error> {“code”: <string>,“message”: <string>}

5xx |  application/vnd.microsoft.error <Error> {“code”: <string>,“message”: <string>}
 */

        private AdaptiveCardInvokeResponse OkWithMessageResponse()
        {
                return new AdaptiveCardInvokeResponse() { StatusCode = 200, Type = AdaptiveCardsConstants.Message, Value = "This is an error message string." };
        }

        private AdaptiveCardInvokeResponse OkWithCardResponse()
        {
            //return CardResponse("BlandCard.json");
            return new AdaptiveCardInvokeResponse() { StatusCode = 200, Type = AdaptiveCard.ContentType, Value = new CardResource("BlandCard.json").AsJObject() };
        }

        private AdaptiveCardInvokeResponse ThrottleWarningResponse()
        {
            return new AdaptiveCardInvokeResponse() { StatusCode = 429, Type = "application/vnd.microsoft.activity.retryAfter", Value = 15 };
        }
        private AdaptiveCardInvokeResponse TeapotResponse()
        {
            return new AdaptiveCardInvokeResponse() { StatusCode = 418, Type = AdaptiveCardsConstants.Error, Value = new Error("418", "I am a little teapot.") };
        }

        private AdaptiveCardInvokeResponse BotErrorResponse()
        {
            return new AdaptiveCardInvokeResponse() { StatusCode = 418, Type = AdaptiveCardsConstants.Error, Value = new Error("500", "Bot has encountered an error.") };
        }

        #endregion
    }

    public class OAuthCommands
    {
        public bool StartedNominalAuth { get; set; }

        public bool StartedSSOAuth { get; set; }
    }
}

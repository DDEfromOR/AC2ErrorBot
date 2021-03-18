// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using AdaptiveCards.Templating;
using Newtonsoft.Json;
using Catering.Cards;
using Catering.Models;
using System.Net;
using AdaptiveCards;
using Newtonsoft.Json.Linq;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Bot.AdaptiveCards;
using System.Linq;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Bot.Connector.Authentication;

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

    public class CateringBot<TDialog> : ActivityHandler where TDialog : Dialog
    {
        private const string WelcomeText = "Welcome to the Adaptive Cards 2.0 Bot. This bot will introduce you to Action.Execute in Adaptive Cards.";
        private BotState _userState;
        private CateringDb _cateringDb;
        private readonly CateringRecognizer _cateringRecognizer;
        private readonly Dialog _dialog;
        private readonly AdaptiveCardOAuthHandler _botAppHandler = new AdaptiveCardOAuthHandler("BotApp", "Sign-In To Bot App", "Sign-In");
        private readonly AdaptiveCardOAuthHandler _nonSsoHandler = new AdaptiveCardOAuthHandler("NonSsoApp", "Sign-In To Bot App", "Sign-In");
        private readonly IConfiguration _configuration;

        public CateringBot(IConfiguration configuration, UserState userState, CateringDb cateringDb, CateringRecognizer cateringRecognizer, TDialog dialog)
        {
            _configuration = configuration;
            _userState = userState;
            _cateringDb = cateringDb;
            _cateringRecognizer = cateringRecognizer;
            _dialog = dialog;

            var oauthCredential = new MicrosoftAppCredentials(
                _configuration.GetSection("MicrosoftAppId")?.Value,
                _configuration.GetSection("MicrosoftAppPassword")?.Value);
            _botAppHandler = new AdaptiveCardOAuthHandler("BotApp", "Sign-In To Bot App", "Sign-In", oauthCredential);
            _nonSsoHandler = new AdaptiveCardOAuthHandler("NonSsoApp", "Sign-In To Bot App", "Sign-In", oauthCredential);
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
            await _dialog.RunAsync(turnContext, _userState.CreateProperty<DialogState>(nameof(DialogState)), cancellationToken);
        }
        protected override async Task OnEndOfConversationActivityAsync(ITurnContext<IEndOfConversationActivity> turnContext, CancellationToken cancellationToken)
        {
            await _dialog.RunAsync(turnContext, _userState.CreateProperty<DialogState>(nameof(DialogState)), cancellationToken);
        }

        protected override async Task OnEventActivityAsync(ITurnContext<IEventActivity> turnContext, CancellationToken cancellationToken)
        {
            await _dialog.RunAsync(turnContext, _userState.CreateProperty<DialogState>(nameof(DialogState)), cancellationToken);
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
            else if (AdaptiveCardInvokeValidator.IsAdaptiveCardAction(turnContext))
            {
                var userSA = _userState.CreateProperty<User>(nameof(User));
                var user = await userSA.GetAsync(turnContext, () => new User() { Id = turnContext.Activity.From.Id });

                try
                {
                    AdaptiveCardInvoke request = AdaptiveCardInvokeValidator.ValidateRequest(turnContext);
                    var cardOptions = AdaptiveCardInvokeValidator.ValidateAction<CardOptions>(request);

                    if (request.Action.Verb == "order")
                    {
                        // process action
                        var responseBody = await ProcessOrderAction(user, cardOptions);

                        return CreateInvokeResponse(HttpStatusCode.OK, responseBody);
                    }

                    if (request.Action.Verb == "err")
                    {
                        var responseBody = await ProcessErrAction(user, cardOptions);

                        return CreateInvokeResponse(HttpStatusCode.OK, responseBody);
                    }
                    else if (request.Action.Verb == "nominal-oauth")
                    {
                        oauthCommands.StartedNominalAuth = true;
                        await oauthCommandsProperty.SetAsync(turnContext, oauthCommands);

                        var responseBody = await ProcessNominalOAuth(turnContext);

                        return CreateInvokeResponse(HttpStatusCode.OK, responseBody);
                    }
                    else if (request.Action.Verb == "sso-oauth")
                    {
                        if (request.Authentication != null && !string.IsNullOrEmpty(request.Authentication.Token))
                        {
                            // already started the oauth flow
                            oauthCommands.StartedSSOAuth = false;
                            await oauthCommandsProperty.SetAsync(turnContext, oauthCommands);

                            var result = await _botAppHandler.GetUserTokenAsync(turnContext, _userState);

                            return CreateInvokeResponse(HttpStatusCode.OK, new AdaptiveCardInvokeResponse()
                            {
                                StatusCode = 200,
                                Type = AdaptiveCardsConstants.Message,
                                Value = $"Completed SSO token exchange and now have a user token: ${result.TokenResponse.Token}"
                            });
                        }
                        else
                        {
                            oauthCommands.StartedSSOAuth = true;
                            await oauthCommandsProperty.SetAsync(turnContext, oauthCommands);

                            var responseBody = await ProcessSSOOAuth(turnContext);

                            return CreateInvokeResponse(HttpStatusCode.OK, responseBody);
                        }
                    }
                    else if (request.Action.Verb == "signout")
                    {
                        var responseBody = await ProcessSignout(turnContext);

                        return CreateInvokeResponse(HttpStatusCode.OK, responseBody);
                    }
                    else
                    {
                        AdaptiveCardActionException.VerbNotSupported(request.Action.Type);
                    }
                }
                catch (AdaptiveCardActionException e)
                {
                    return CreateInvokeResponse(HttpStatusCode.OK, e.Response);
                }
            }

            return null;
        }

        private async Task<AdaptiveCardInvokeResponse> ProcessOrderAction(User user, CardOptions cardOptions)
        {
            if ((Card)cardOptions.currentCard == Card.Entre)
            {
                if (!string.IsNullOrEmpty(cardOptions.custom))
                {
                    if (!await _cateringRecognizer.ValidateEntre(cardOptions.custom))
                    {
                        return RedoEntreCardResponse(new Lunch() { Entre = cardOptions.custom });
                    }
                    cardOptions.option = cardOptions.custom;
                }

                user.Lunch.Entre = cardOptions.option;
            }
            else if ((Card)cardOptions.currentCard == Card.Drink)
            {
                if (!string.IsNullOrEmpty(cardOptions.custom))
                {
                    if (!await _cateringRecognizer.ValidateDrink(cardOptions.custom))
                    {
                        return RedoDrinkCardResponse(new Lunch() { Drink = cardOptions.custom });
                    }

                    cardOptions.option = cardOptions.custom;
                }

                user.Lunch.Drink = cardOptions.option;
            }

            AdaptiveCardInvokeResponse responseBody = null;
            switch ((Card)cardOptions.nextCardToSend)
            {
                case Card.Drink:
                    responseBody = DrinkCardResponse();
                    break;
                case Card.Entre:
                    responseBody = EntreCardResponse();
                    break;
                case Card.Review:
                    responseBody = ReviewCardResponse(user);
                    break;
                case Card.ReviewAll:
                    var latestOrders = await _cateringDb.GetRecentOrdersAsync();
                    responseBody = RecentOrdersCardResponse(latestOrders.Items);
                    break;
                case Card.Confirmation:
                    await _cateringDb.UpsertOrderAsync(user);
                    responseBody = ConfirmationCardResponse();
                    break;
                default:
                    throw new NotImplementedException("No card matches that nextCardToSend.");
            }

            return responseBody;
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

        private AdaptiveCardInvokeResponse CardResponse<T>(string cardFileName, T data)
        {
            return new AdaptiveCardInvokeResponse()
            {
                StatusCode = 200,
                Type = AdaptiveCard.ContentType,
                Value = new CardResource(cardFileName).AsJObject(data)
            };
        }

        private AdaptiveCardInvokeResponse DrinkCardResponse()
        {
            return CardResponse("DrinkOptions.json");
        }

        private AdaptiveCardInvokeResponse EntreCardResponse()
        {
            return CardResponse("EntreOptions.json");
        }

        private AdaptiveCardInvokeResponse ReviewCardResponse(User user)
        {
            return CardResponse("ReviewOrder.json", user.Lunch);
        }

        private AdaptiveCardInvokeResponse RedoDrinkCardResponse(Lunch lunch)
        {
            return CardResponse("RedoDrinkOptions.json", lunch);
        }

        private AdaptiveCardInvokeResponse RedoEntreCardResponse(Lunch lunch)
        {
            return CardResponse("RedoEntreOptions.json", lunch);
        }

        private AdaptiveCardInvokeResponse RecentOrdersCardResponse(IList<User> users)
        {
            return CardResponse("RecentOrders.json",
                new
                {
                    users = users.Select(u => new
                    {
                        lunch = new
                        {
                            entre = u.Lunch.Entre,
                            drink = u.Lunch.Drink,
                            orderTimestamp = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(u.Lunch.OrderTimestamp, "Pacific Standard Time").ToString("g")
                        }
                    }).ToList()
                });
        }

        private AdaptiveCardInvokeResponse ConfirmationCardResponse()
        {
            return CardResponse("Confirmation.json");
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

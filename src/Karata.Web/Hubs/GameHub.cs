#nullable enable

using System.Collections.Concurrent;
using System.Text;
using Karata.Web.Engines;
using Karata.Web.Hubs.Clients;
using Karata.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using static Karata.Cards.Card.CardFace;

namespace Karata.Web.Hubs;

/** 
  *  Implemented herein is a mechanism to prompt for values from the frontend.
  *  REF: https://docs.microsoft.com/en-us/dotnet/standard/parallel-programming/how-to-wrap-eap-patterns-in-a-task
  *  REF: https://devblogs.microsoft.com/premier-developer/the-danger-of-the-taskcompletionsourcet-class
  *  REF: https://github.com/SignalR/SignalR/issues/1149#issuecomment-302611992
  */
[Authorize]
public class GameHub : Hub<IGameClient>
{
    private readonly IEngine _engine;
    private readonly ILogger<GameHub> _logger;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordService _passwordService;
    private readonly IRoomService _roomService;
    private readonly UserManager<ApplicationUser> _userManager;
    public static readonly ConcurrentDictionary<string, TaskCompletionSource<Card>> CardRequests = new();
    public static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> LastCardRequests = new();

    public GameHub(
        IEngine engine,
        ILogger<GameHub> logger,
        IPasswordService passwordService,
        IUnitOfWork unitOfWork,
        UserManager<ApplicationUser> userManager)
    {
        _engine = engine;
        _logger = logger;
        _passwordService = passwordService;
        _unitOfWork = unitOfWork;
        _roomService = _unitOfWork.RoomService;
        _userManager = userManager;
    }

    public async Task SendChat(string inviteLink, string text)
    {
        var user = await _userManager.FindByEmailAsync(Context.UserIdentifier);
        var room = await _roomService.FindByInviteLinkAsync(inviteLink);
        var message = new Chat { Text = text, Sender = user };

        room.Chats.Add(message);
        await Clients.Group(inviteLink).ReceiveChat(message);
        await _unitOfWork.CompleteAsync();
    }

    public async Task CreateRoom(string? password)
    {
        // Create room
        var user = await _userManager.FindByEmailAsync(Context.UserIdentifier);
        var room = new Room
        {
            InviteLink = Guid.NewGuid().ToString(),
            Creator = user,
        };

        if (!string.IsNullOrWhiteSpace(password))
        {
            room.Salt = PasswordService.GenerateSalt();
            room.Hash = _passwordService.HashPassword(Encoding.UTF8.GetBytes(password), room.Salt);
        }

        await _roomService.CreateAsync(room);

        // Add creator to game
        room.Game.Players.Add(user);
        await Clients.OthersInGroup(room.InviteLink).AddPlayerToRoom(user);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.InviteLink);
        await Clients.Caller.AddToRoom(room);
        await _unitOfWork.CompleteAsync();
    }

    public async Task JoinRoom(string inviteLink, string? password)
    {
        var room = await _roomService.FindByInviteLinkAsync(inviteLink);
        var game = room.Game;
        
        if (room.Hash is not null)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                var message = new SystemMessage("You need to enter a password to join this room.", MessageType.Error);
                await Clients.Caller.ReceiveSystemMessage(message);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(password);
            if (!_passwordService.VerifyPassword(bytes, room.Salt!, room.Hash))
            {
                var message = new SystemMessage("The password you entered is incorrect.", MessageType.Error);
                await Clients.Caller.ReceiveSystemMessage(message);
                return;
            }
        }

        // Check game status
        if (game.IsStarted)
        {
            var message = new SystemMessage("This game has already started.", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            return;
        }

        // Check player count
        if (game.Players.Count >= 4)
        {
            var message = new SystemMessage("This game is full.", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            return;
        }

        // Add player to room
        var user = await _userManager.FindByEmailAsync(Context.UserIdentifier);
        game.Players.Add(user);
        await Clients.OthersInGroup(inviteLink).AddPlayerToRoom(user);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.InviteLink!);
        await Clients.Caller.AddToRoom(room);
        await _unitOfWork.CompleteAsync();
    }

    public async Task LeaveRoom(string inviteLink)
    {
        var user = await _userManager.FindByEmailAsync(Context.UserIdentifier);
        var room = await _roomService.FindByInviteLinkAsync(inviteLink);
        var game = room.Game;       

        // Check game status
        if (game.IsStarted || room.Creator!.Id == user.Id)
        {
            // TODO: Handle this gracefully, as well as accidental disconnection.
            var message = new SystemMessage("Please don't do this. The game isn't built to handle it.", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            return;
        }

        // Remove player from room
        game.Players.Remove(user);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, room.InviteLink!);
        await Clients.Caller.RemoveFromRoom();
        await Clients.OthersInGroup(room.InviteLink!).RemovePlayerFromRoom(user);
        await _unitOfWork.CompleteAsync();
    }

    public async Task StartGame(string inviteLink)
    {
        var room = await _roomService.FindByInviteLinkAsync(inviteLink);
        var game = room.Game;

        // Check caller role
        if (room.Creator!.Email != Context.UserIdentifier)
        {
            var message = new SystemMessage("You are not allowed to perform that action.", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            return;
        };

        // Check game status
        if (game.IsStarted)
        {
            var message = new SystemMessage("This game has already begun.", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            return;
        };

        // Check player number
        if (game.Players.Count < 2 || game.Players.Count > 4)
        {
            var message = new SystemMessage("A game needs 2-4 players.", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            return;
        };

        // Shuffle deck
        var deck = game.Deck;

        // Deal starting card
        Card? topCard = null;
        do
        {
            if (topCard is not null)
                deck.Push(topCard);
            deck.Shuffle();
            topCard = deck.Deal();
        }
        while (!IsBoring(topCard));

        await Clients.Group(inviteLink).RemoveCardsFromDeck(1);
        game.Pile.Push(topCard);
        await Clients.Group(inviteLink).AddCardToPile(topCard);

        // Deal player cards
        // TODO: Explicit card movements (Deck -> Hand, Hand -> Pile, etc).
        foreach (var player in game.Players)
        {
            player.Hand.Clear();
            await Clients.User(player.Email).EmptyHand();

            uint dealtCount = 4;

            var dealt = deck.DealMany(dealtCount);
            await Clients.Group(inviteLink).RemoveCardsFromDeck(dealtCount);

            player.Hand.AddRange(dealt);
            await Clients.User(player.Email).AddCardRangeToHand(dealt);
        }

        // Start game
        game.IsStarted = true;
        await Clients.Group(inviteLink).UpdateGameStatus(true);
        await _unitOfWork.CompleteAsync();
    }

    public async Task PerformTurn(string inviteLink, List<Card> cardList)
    {
        // Setup
        var room = await _roomService.FindByInviteLinkAsync(inviteLink);
        var game = room.Game;
        var currentTurn = game.CurrentTurn;
        var player = game.Players[currentTurn];
        var turn = new Turn(player.Id, cardList);
        game.Turns.Add(turn);

        // Check for ukora
        if (CardRequests.ContainsKey(Context.ConnectionId) || LastCardRequests.ContainsKey(Context.ConnectionId))
        {
            var message = new SystemMessage("You can't perform a turn while you have a pending request.", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            return;
        }

        // Check game status
        if (!game.IsStarted)
        {
            var message = new SystemMessage("The game has not started yet.", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            await Clients.Caller.NotifyTurnProcessed(valid: false);
            return;
        }

        // Check turn
        if (player.Email != Context.UserIdentifier)
        {
            var message = new SystemMessage("It is not your turn!", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            await Clients.Caller.NotifyTurnProcessed(valid: false);
            return;
        }

        // Cards from last turn
        game.Pick = game.Give;
        game.Give = 0;

        // Process turn
        if (!_engine.ValidateTurnCards(game, cardList))
        {
            var message = new SystemMessage("That card sequence is invalid!", MessageType.Error);
            await Clients.Caller.ReceiveSystemMessage(message);
            await Clients.Caller.NotifyTurnProcessed(valid: false);
            return;
        }

        // Add cards to pile
        foreach (var card in cardList)
        {
            game.Pile.Push(card);
            await Clients.Group(inviteLink).AddCardToPile(card);
        }

        // Remove cards from player hand
        await Clients.Caller.NotifyTurnProcessed(valid: true);
        player.Hand.RemoveAll(card => cardList.Contains(card));

        // Generate delta and update game state.
        var delta = _engine.GenerateTurnDelta(game, cardList);

        // Remove request
        if (delta.RemovesPreviousRequest)
        {
            game.CurrentRequest = null;
            await Clients.Group(inviteLink).SetCurrentRequest(null);
        }

        if (delta.HasRequest)
        {
            var tcs = new TaskCompletionSource<Card>(TaskCreationOptions.RunContinuationsAsynchronously);
            CardRequests.TryAdd(Context.ConnectionId, tcs);
            await Clients.Caller.PromptCardRequest(delta.HasSpecificRequest);

            try
            {
                // TODO: Cancel this task if the client disconnects (potentially by just adding a timeout)
                var request = await tcs.Task; // Wait for the client to respond
                game.CurrentRequest = request;
                turn.Request = request;
                await Clients.Group(inviteLink).SetCurrentRequest(request);
            }
            finally
            {
                CardRequests.TryRemove(Context.ConnectionId, out tcs);
            }
        }

        if (delta.Reverse) game.IsForward = !game.IsForward;
        game.Give = delta.Give;
        game.Pick = delta.Pick;

        // Check whether there are cards to pick.
        if (game.Pick > 0)
        {
            // Remove card from pile
            if (!game.Deck.TryDealMany(game.Pick, out var cards))
            {
                if (game.Pile.Count + game.Deck.Count - 1 > game.Pick)
                {
                    // Remove cards from pile
                    var pileCards = game.Pile.Reclaim();
                    await Clients.Group(inviteLink).ReclaimPile();

                    // Add cards to deck
                    foreach (var pileCard in pileCards) game.Deck.Push(pileCard);
                    await Clients.Group(inviteLink).AddCardsToDeck((uint)pileCards.Count);

                    // Shuffle & deal
                    game.Deck.Shuffle();
                    cards = game.Deck.DealMany(game.Pick);
                }
                else
                {
                    await Clients.Caller.NotifyTurnProcessed(valid: true);
                    await Clients.Group(inviteLink).EndGame(winner: null);
                    // TODO: Remove everyone from the game.
                    // await Groups.RemoveFromGroupAsync(Context.ConnectionId, inviteLink);
                    return;
                }
            };
            await Clients.Group(inviteLink).RemoveCardsFromDeck(1);

            // Add cards to player hand and reset counter
            player.Hand.AddRange(cards!);
            await Clients.Caller.AddCardRangeToHand(cards!);            
            game.Pick = 0;
        }

        // Check whether the game is over.
        if (player.Hand.Count == 0)
        {
            if (player.IsLastCard && IsBoring(cardList[^1]))
            {
                await Clients.Caller.NotifyTurnProcessed(valid: true);
                game.Winner = player;
                await Clients.Group(inviteLink).EndGame(winner: player);
                // TODO: Remove everyone from the room (store connection ids)
                // await Groups.RemoveFromGroupAsync(Context.ConnectionId, inviteLink);
                await _unitOfWork.CompleteAsync();
                return;
            }
            else 
            {
                var message = new SystemMessage($"{player.Email} is cardless.", MessageType.Info);
                await Clients.OthersInGroup(inviteLink).ReceiveSystemMessage(message);
            }
        }     
        else
        {
            // TODO: Player cannot be on their last card if they have a  Ace, "Bomb", Jack or King
            var lastCardTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = LastCardRequests.TryAdd(Context.ConnectionId, lastCardTcs);
            await Clients.Caller.PromptLastCardRequest();

            try
            {  
                // TODO: Cancel this task if the client disconnects (potentially by just adding a timeout)
                var isLastCard = await lastCardTcs.Task; // Wait for the client to respond
                player.IsLastCard = isLastCard;
                if (isLastCard) 
                { 
                    turn.IsLastCard = true;
                    var message = new SystemMessage($"{player.Email} is on their last card.", MessageType.Warning);
                    await Clients.OthersInGroup(inviteLink).ReceiveSystemMessage(message);
                }
            }
            finally
            {
                LastCardRequests.TryRemove(Context.ConnectionId, out lastCardTcs);
            }
        }

        // Next turn
        var lastIndex = game.Players.Count - 1;
        for (uint i = 0; i < delta.Skip; i++)
        {
            if (game.IsForward) 
                game.CurrentTurn = currentTurn == lastIndex ? 0 : ++currentTurn;
            else 
                game.CurrentTurn = currentTurn == 0 ? lastIndex : --currentTurn;
        }
        await Clients.Group(inviteLink).UpdateTurn(game.CurrentTurn);
        await _unitOfWork.CompleteAsync();
    }

    public void RequestCard(Card request)
    {
        if (CardRequests.TryGetValue(Context.ConnectionId, out var tcs))
        {
            // Trigger the task continuation
            tcs.TrySetResult(request);
        }
        else
        {
            // Client response for something that isn't being tracked, might be an error
        }
    }

    public void SetLastCardStatus(bool lastCard)
    {
        if (LastCardRequests.TryGetValue(Context.ConnectionId, out var tcs))
        {
            // Trigger the task continuation
            tcs.TrySetResult(lastCard);
        }
        else
        {
            // Client response for something that isn't being tracked, might be an error
        }
    }

    private static bool IsBoring(Card card) =>
        !card.IsBomb()
        && !card.IsQuestion()
        && card is not { Face: Ace or Jack or King };
}
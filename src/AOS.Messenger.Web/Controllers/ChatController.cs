using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

[Authorize]
public sealed class ChatController : Controller
{
    private readonly IConversationService _conversationService;
    private readonly IMessageService _messageService;
    private readonly ICurrentUser _currentUser;

    public ChatController(
        IConversationService conversationService,
        IMessageService messageService,
        ICurrentUser currentUser)
    {
        _conversationService = conversationService;
        _messageService = messageService;
        _currentUser = currentUser;
    }

    // Screen 1: A♡S Desktop App Shell - Light Theme (Main active messenger view)
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        var conversations = await _conversationService.GetConversationListAsync(
            _currentUser.Id,
            includeArchived: false,
            ct);

        return View(new ChatIndexViewModel
        {
            Conversations = conversations,
            SelectedConversationId = null
        });
    }

    // Screen 2: A♡S Messenger - 1-to-1 Active Chat (Light Theme)
    [HttpGet("chat/{id:guid}")]
    [HttpGet("Chat/Conversation/{id:guid}")]
    [HttpGet("Conversation/{id:guid}")]
    public async Task<IActionResult> Conversation(Guid id, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        if (id == Guid.Empty)
        {
            return RedirectToAction(nameof(Index));
        }

        var conversationId = new ConversationId(id);
        var conversation = await _conversationService.GetConversationAsync(_currentUser.Id, conversationId, ct);
        var conversations = await _conversationService.GetConversationListAsync(_currentUser.Id, includeArchived: false, ct);
        var messagesPage = await _messageService.GetMessagesAsync(_currentUser.Id, conversationId, null, 30, ct);

        return View(new ChatConversationViewModel
        {
            Conversation = conversation,
            Conversations = conversations,
            Messages = messagesPage.Messages.OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).ToList(),
            NextCursor = messagesPage.NextCursor
        });
    }

    [HttpPost("Chat/SendMessage")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> SendMessage(
        [FromForm] Guid conversationId,
        [FromForm] string content,
        [FromForm] long? replyToMessageId = null,
        [FromForm] string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return RedirectToAction(nameof(Conversation), new { id = conversationId });
        }

        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : idempotencyKey;

        var replyId = replyToMessageId.HasValue ? new MessageId(replyToMessageId.Value) : (MessageId?)null;
        var dto = new SendMessageRequestDto(content.Trim(), key, replyId);

        await _messageService.SendMessageAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            dto,
            ct);

        return RedirectToAction(nameof(Conversation), new { id = conversationId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> Start([FromForm] Guid targetUserId, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        var conversation = await _conversationService.GetOrCreateConversationAsync(
            _currentUser.Id,
            new UserId(targetUserId),
            ct);

        return RedirectToAction(nameof(Conversation), new { id = conversation.Id.Value });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pin([FromForm] Guid conversationId, [FromForm] bool isPinned, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        await _conversationService.PinConversationAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            isPinned,
            ct);

        return RedirectToAction(nameof(Conversation), new { id = conversationId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive([FromForm] Guid conversationId, [FromForm] bool isArchived, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        await _conversationService.ArchiveConversationAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            isArchived,
            ct);

        if (isArchived)
        {
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(Archived));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Mute([FromForm] Guid conversationId, [FromForm] bool isMuted, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        await _conversationService.MuteConversationAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            isMuted,
            ct);

        return RedirectToAction(nameof(Conversation), new { id = conversationId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear([FromForm] Guid conversationId, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        await _conversationService.ClearChatAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            ct);

        return RedirectToAction(nameof(Conversation), new { id = conversationId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete([FromForm] Guid conversationId, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        await _conversationService.DeleteConversationAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            ct);

        return RedirectToAction(nameof(Index));
    }

    // Screen 4: A♡S Messenger - Archived Conversations State
    [HttpGet]
    public async Task<IActionResult> Archived(CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        var conversations = await _conversationService.GetConversationListAsync(
            _currentUser.Id,
            includeArchived: true,
            ct);

        return View(new ArchivedChatsViewModel
        {
            ArchivedConversations = conversations.Where(c => c.IsArchived).ToList()
        });
    }

    // Screen 3: A♡S Desktop App Shell - Dark Theme
    [HttpGet]
    public IActionResult DarkTheme()
    {
        return View();
    }

    // Screen 5: A♡S Messenger - Mobile 1-to-1 Chat
    [HttpGet]
    public IActionResult Mobile()
    {
        return View();
    }

    // Screen 6: A♡S Messenger - Voice Recording Workflow
    [HttpGet]
    public IActionResult VoiceRecording()
    {
        return View();
    }

    // Screen 7: A♡S Messenger - Attachment Workflow & Previews
    [HttpGet]
    public IActionResult Attachments()
    {
        return View();
    }

    // Screen 8: A♡S Messenger - New Conversation Modal
    [HttpGet]
    public IActionResult NewConversation()
    {
        return View();
    }

    [HttpGet("Chat/New")]
    public IActionResult New()
    {
        return RedirectToAction(nameof(NewConversation));
    }

    // Screen 9: A♡S Messenger - Delete Conversation Modal
    [HttpGet]
    public IActionResult DeleteModal()
    {
        return View();
    }

    // Screen 10: A♡S Messenger - Conversation List Context Menu
    [HttpGet]
    public IActionResult ContextMenu()
    {
        return View();
    }
}

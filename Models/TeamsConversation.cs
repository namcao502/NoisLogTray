namespace NoisLogTray;

// A ticket's post thread (ChatId null, empty Title) or one of its group chats, as the background read returns it.
internal sealed record TeamsConversation(string TicketKey, string? ChatId, string Title, TeamsThread Thread);

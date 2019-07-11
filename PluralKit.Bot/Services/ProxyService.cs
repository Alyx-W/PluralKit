using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using Discord;
using Discord.Webhook;
using Discord.WebSocket;

namespace PluralKit.Bot
{
    class ProxyDatabaseResult
    {
        public PKSystem System;
        public PKMember Member;
    }

    class ProxyMatch {
        public PKMember Member;
        public PKSystem System;
        public string InnerText;

        public string ProxyName => Member.Name + (System.Tag != null ? " " + System.Tag : "");
    }

    class ProxyService {
        private IDiscordClient _client;
        private DbConnectionFactory _conn;
        private LogChannelService _logger;
        private WebhookCacheService _webhookCache;
        private MessageStore _messageStorage;
        private EmbedService _embeds;
        
        public ProxyService(IDiscordClient client, WebhookCacheService webhookCache, DbConnectionFactory conn, LogChannelService logger, MessageStore messageStorage, EmbedService embeds)
        {
            _client = client;
            _webhookCache = webhookCache;
            _conn = conn;
            _logger = logger;
            _messageStorage = messageStorage;
            _embeds = embeds;
        }

        private ProxyMatch GetProxyTagMatch(string message, IEnumerable<ProxyDatabaseResult> potentials)
        {
            // If the message starts with a @mention, and then proceeds to have proxy tags,
            // extract the mention and place it inside the inner message
            // eg. @Ske [text] => [@Ske text]
            int matchStartPosition = 0;
            string leadingMention = null;
            if (Utils.HasMentionPrefix(message, ref matchStartPosition))
            {
                leadingMention = message.Substring(0, matchStartPosition);
                message = message.Substring(matchStartPosition);
            }

            // Sort by specificity (ProxyString length desc = prefix+suffix length desc = inner message asc = more specific proxy first!)
            var ordered = potentials.OrderByDescending(p => p.Member.ProxyString.Length);
            foreach (var potential in ordered)
            {
                if (potential.Member.Prefix == null && potential.Member.Suffix == null) continue;
                
                var prefix = potential.Member.Prefix ?? "";
                var suffix = potential.Member.Suffix ?? "";

                if (message.StartsWith(prefix) && message.EndsWith(suffix)) {
                    var inner = message.Substring(prefix.Length, message.Length - prefix.Length - suffix.Length);
                    if (leadingMention != null) inner = $"{leadingMention} {inner}";
                    return new ProxyMatch { Member = potential.Member, System = potential.System, InnerText = inner };
                }
            }
            
            return null;
        }

        public async Task HandleMessageAsync(IMessage message)
        {
            IEnumerable<ProxyDatabaseResult> results;
            using (var conn = _conn.Obtain())
            {
                results = await conn.QueryAsync<PKMember, PKSystem, ProxyDatabaseResult>(
                    "select members.*, systems.* from members, systems, accounts where members.system = systems.id and accounts.system = systems.id and accounts.uid = @Uid",
                    (member, system) =>
                        new ProxyDatabaseResult {Member = member, System = system}, new {Uid = message.Author.Id});
            }

            // Find a member with proxy tags matching the message
            var match = GetProxyTagMatch(message.Content, results);
            if (match == null) return;

            // We know message.Channel can only be ITextChannel as PK doesn't work in DMs/groups
            // Afterwards we ensure the bot has the right permissions, otherwise bail early
            if (!await EnsureBotPermissions(message.Channel as ITextChannel)) return;

            // Fetch a webhook for this channel, and send the proxied message
            var webhook = await _webhookCache.GetWebhook(message.Channel as ITextChannel);
            var hookMessage = await ExecuteWebhook(webhook, match.InnerText, match.ProxyName, match.Member.AvatarUrl, message.Attachments.FirstOrDefault());

            // Store the message in the database, and log it in the log channel (if applicable)
            await _messageStorage.Store(message.Author.Id, hookMessage.Id, hookMessage.Channel.Id, match.Member);
            await _logger.LogMessage(match.System, match.Member, hookMessage, message.Author);

            // Wait a second or so before deleting the original message
            await Task.Delay(1000);
            await message.DeleteAsync();
        }

        private async Task<bool> EnsureBotPermissions(ITextChannel channel)
        {
            var guildUser = await channel.Guild.GetCurrentUserAsync();
            var permissions = guildUser.GetPermissions(channel);

            if (!permissions.ManageWebhooks)
            {
                await channel.SendMessageAsync(
                    $"{Emojis.Error} PluralKit does not have the *Manage Webhooks* permission in this channel, and thus cannot proxy messages. Please contact a server administrator to remedy this.");
                return false;
            }
            
            if (!permissions.ManageMessages)
            {
                await channel.SendMessageAsync(
                    $"{Emojis.Error} PluralKit does not have the *Manage Messages* permission in this channel, and thus cannot delete the original trigger message. Please contact a server administrator to remedy this.");
                return false;
            }

            return true;
        }

        private async Task<IMessage> ExecuteWebhook(IWebhook webhook, string text, string username, string avatarUrl, IAttachment attachment) {
            // TODO: DiscordWebhookClient's ctor does a call to GetWebhook that may be unnecessary, see if there's a way to do this The Hard Way :tm:
            // TODO: this will probably crash if there are multiple consecutive failures, perhaps have a loop instead?
            DiscordWebhookClient client;
            try
            {
                client = new DiscordWebhookClient(webhook);
            }
            catch (InvalidOperationException)
            {
                // webhook was deleted or invalid
                webhook = await _webhookCache.InvalidateAndRefreshWebhook(webhook);
                client = new DiscordWebhookClient(webhook);
            }

            ulong messageId;
            if (attachment != null) {
                using (var http = new HttpClient())
                using (var stream = await http.GetStreamAsync(attachment.Url)) {
                    messageId = await client.SendFileAsync(stream, filename: attachment.Filename, text: text, username: username, avatarUrl: avatarUrl);
                }
            } else {
                messageId = await client.SendMessageAsync(text, username: username, avatarUrl: avatarUrl);
            }
            
            // TODO: SendMessageAsync should return a full object(??), see if there's a way to avoid the extra server call here
            return await webhook.Channel.GetMessageAsync(messageId);
        }

        public Task HandleReactionAddedAsync(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            // Dispatch on emoji
            switch (reaction.Emote.Name)
            {
                case "\u274C": // Red X
                    return HandleMessageDeletionByReaction(message, reaction.UserId);
                case "\u2753": // Red question mark
                case "\u2754": // White question mark
                    return HandleMessageQueryByReaction(message, reaction.UserId);
                default:
                    return Task.CompletedTask;
            }
        }

        private async Task HandleMessageQueryByReaction(Cacheable<IUserMessage, ulong> message, ulong userWhoReacted)
        {
            var user = await _client.GetUserAsync(userWhoReacted);
            if (user == null) return;

            var msg = await _messageStorage.Get(message.Id);
            if (msg == null) return;
            
            await user.SendMessageAsync(embed: await _embeds.CreateMessageInfoEmbed(msg));
        }

        public async Task HandleMessageDeletionByReaction(Cacheable<IUserMessage, ulong> message, ulong userWhoReacted)
        {
            // Find the message in the database
            var storedMessage = await _messageStorage.Get(message.Id);
            if (storedMessage == null) return; // (if we can't, that's ok, no worries)

            // Make sure it's the actual sender of that message deleting the message
            if (storedMessage.Message.Sender != userWhoReacted) return;

            try {
                // Then, fetch the Discord message and delete that
                // TODO: this could be faster if we didn't bother fetching it and just deleted it directly
                // somehow through REST?
                await (await message.GetOrDownloadAsync()).DeleteAsync();
            } catch (NullReferenceException) {
                // Message was deleted before we got to it... cool, no problem, lmao
            }

            // Finally, delete it from our database.
            await _messageStorage.Delete(message.Id);
        }

        public async Task HandleMessageDeletedAsync(Cacheable<IMessage, ulong> message, ISocketMessageChannel channel)
        {
            await _messageStorage.Delete(message.Id);
        }
    }
}
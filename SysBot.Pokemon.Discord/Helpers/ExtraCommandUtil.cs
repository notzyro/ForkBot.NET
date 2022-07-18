﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using PKHeX.Core;

namespace SysBot.Pokemon.Discord
{
    public class ExtraCommandUtil<T> where T : PKM, new()
    {
        private static readonly Dictionary<ulong, ReactMessageContents> ReactMessageDict = new();
        private static bool DictWipeRunning = false;

        private class ReactMessageContents
        {
            public List<string> Pages { get; set; } = new();
            public EmbedBuilder Embed { get; set; } = new();
            public ulong MessageID { get; set; }
            public DateTime EntryTime { get; set; }
        }

        public async Task ListUtil(SocketCommandContext ctx, string nameMsg, string entry)
        {
            List<string> pageContent = ListUtilPrep(entry);
            bool canReact = ctx.Guild.CurrentUser.GetPermissions(ctx.Channel as IGuildChannel).AddReactions;
            var embed = new EmbedBuilder { Color = GetBorderColor(false) }.AddField(x =>
            {
                x.Name = nameMsg;
                x.Value = pageContent[0];
                x.IsInline = false;
            }).WithFooter(x =>
            {
                x.IconUrl = "https://i.imgur.com/nXNBrlr.png";
                x.Text = $"Page 1 of {pageContent.Count}";
            });

            if (!canReact && pageContent.Count > 1)
            {
                embed.AddField(x =>
                {
                    x.Name = "Missing \"Add Reactions\" Permission";
                    x.Value = "Displaying only the first page of the list due to embed field limits.";
                });
            }

            var msg = await ctx.Message.Channel.SendMessageAsync(embed: embed.Build()).ConfigureAwait(false);
            if (pageContent.Count > 1 && canReact)
            {
                bool exists = ReactMessageDict.TryGetValue(ctx.User.Id, out _);
                if (exists)
                    ReactMessageDict[ctx.User.Id] = new() { Embed = embed, Pages = pageContent, MessageID = msg.Id, EntryTime = DateTime.Now };
                else ReactMessageDict.Add(ctx.User.Id, new() { Embed = embed, Pages = pageContent, MessageID = msg.Id, EntryTime = DateTime.Now });

                IEmote[] reactions = { new Emoji("⬅️"), new Emoji("➡️"), new Emoji("⬆️"), new Emoji("⬇️") };
                _ = Task.Run(async () => await msg.AddReactionsAsync(reactions).ConfigureAwait(false));
                if (!DictWipeRunning)
                    _ = Task.Run(async () => await DictWipeMonitor().ConfigureAwait(false));
            }
        }

        private static async Task DictWipeMonitor()
        {
            DictWipeRunning = true;
            while (true)
            {
                await Task.Delay(10_000).ConfigureAwait(false);
                for (int i = 0; i < ReactMessageDict.Count; i++)
                {
                    var entry = ReactMessageDict.ElementAt(i);
                    var delta = (DateTime.Now - entry.Value.EntryTime).TotalSeconds;
                    if (delta > 90.0)
                        ReactMessageDict.Remove(entry.Key);
                }
            }
        }

        public static async Task TCUserBanned(SocketUser user, SocketGuild guild)
        {
            if (!TradeCordHelper<T>.TCInitialized)
                return;

            var instance = SysCord<T>.Runner.Hub.Config;
            var helper = new TradeCordHelper<T>(instance.TradeCord);
            var ctx = new TradeCordHelper<T>.TC_CommandContext() { Context = TCCommandContext.DeleteUser, ID = user.Id, Username = user.Username };
            var result =  await helper.ProcessTradeCord(ctx, new string[] { user.Id.ToString() }).ConfigureAwait(false);
            if (result.Success)
            {
                var channels = instance.Discord.EchoChannels.List;
                for (int i = 0; i < channels.Count; i++)
                {
                    ISocketMessageChannel? channel = (ISocketMessageChannel?)guild.Channels.FirstOrDefault(x => x.Id == channels[i].ID);
                    if (channel == default)
                        continue;

                    await channel.SendMessageAsync($"**[TradeCord]** Automatically deleted TradeCord data for: \n**{user.Username}{user.Discriminator}** ({user.Id}) in: **{guild.Name}**.\n Reason: Banned.").ConfigureAwait(false);
                }    
                Base.LogUtil.LogInfo($"Automatically deleted TradeCord data for: {user.Username}{user.Discriminator} ({user.Id}) in: {guild.Name}.", "TradeCord: ");
            }
        }

        public static async Task HandleReactionAsync(Cacheable<IUserMessage, ulong> cachedMsg, Cacheable<IMessageChannel, ulong> ch, SocketReaction reaction)
        {
            IEmote[] reactions = { new Emoji("⬅️"), new Emoji("➡️"), new Emoji("⬆️"), new Emoji("⬇️") };
            if (!reactions.Contains(reaction.Emote))
                return;

            var tc = SysCord<T>.Runner.Hub.Config.Discord.TradeCordChannels.List;
            if (!ch.HasValue || ch.Value is IDMChannel || (tc.Count != 0 && tc.FirstOrDefault(x => x.ID == ch.Id || x.Name == ch.Value.Name) == default))
                return;

            IUserMessage msg;
            if (!cachedMsg.HasValue)
                msg = await cachedMsg.GetOrDownloadAsync().ConfigureAwait(false);
            else msg = cachedMsg.Value;

            bool process = msg.Embeds.Count > 0 && (TradeCordHelper<T>.TCInitialized || msg.Embeds.First().Fields[0].Name.Contains("Giveaway Pool"));
            if (!process || !reaction.User.IsSpecified)
                return;

            var user = reaction.User.Value;
            if (user.IsBot || !ReactMessageDict.ContainsKey(user.Id))
                return;

            bool invoker = msg.Embeds.First().Fields[0].Name == ReactMessageDict[user.Id].Embed.Fields[0].Name;
            if (!invoker)
                return;

            var contents = ReactMessageDict[user.Id];
            bool oldMessage = msg.Id != contents.MessageID;
            if (oldMessage)
                return;

            int page = contents.Pages.IndexOf((string)contents.Embed.Fields[0].Value);
            if (page == -1)
                return;

            if (reaction.Emote.Name == reactions[0].Name || reaction.Emote.Name == reactions[1].Name)
            {
                if (reaction.Emote.Name == reactions[0].Name)
                {
                    if (page == 0)
                        page = contents.Pages.Count - 1;
                    else page--;
                }
                else
                {
                    if (page + 1 == contents.Pages.Count)
                        page = 0;
                    else page++;
                }

                contents.Embed.Fields[0].Value = contents.Pages[page];
                contents.Embed.Footer.Text = $"Page {page + 1} of {contents.Pages.Count}";
                await msg.RemoveReactionAsync(reactions[reaction.Emote.Name == reactions[0].Name ? 0 : 1], user).ConfigureAwait(false);
                await msg.ModifyAsync(msg => msg.Embed = contents.Embed.Build()).ConfigureAwait(false);
            }
            else if (reaction.Emote.Name == reactions[2].Name || reaction.Emote.Name == reactions[3].Name)
            {
                List<string> tempList = new();
                foreach (var p in contents.Pages)
                {
                    var split = p.Replace(", ", ",").Split(',');
                    tempList.AddRange(split);
                }

                var tempEntry = string.Join(", ", reaction.Emote.Name == reactions[2].Name ? tempList.OrderBy(x => x.Split(' ')[1]) : tempList.OrderByDescending(x => x.Split(' ')[1]));
                contents.Pages = ListUtilPrep(tempEntry);
                contents.Embed.Fields[0].Value = contents.Pages[page];
                contents.Embed.Footer.Text = $"Page {page + 1} of {contents.Pages.Count}";
                await msg.RemoveReactionAsync(reactions[reaction.Emote.Name == reactions[2].Name ? 2 : 3], user).ConfigureAwait(false);
                await msg.ModifyAsync(msg => msg.Embed = contents.Embed.Build()).ConfigureAwait(false);
            }
        }

        public async Task<bool> ReactionVerification(SocketCommandContext ctx)
        {
            var sw = new Stopwatch();
            IEmote reaction = new Emoji("👍");
            var msg = await ctx.Channel.SendMessageAsync($"{ctx.User.Username}, please react to the attached emoji in order to confirm you're not using a script.").ConfigureAwait(false);
            await msg.AddReactionAsync(reaction).ConfigureAwait(false);

            sw.Start();
            while (sw.ElapsedMilliseconds < 20_000)
            {
                await msg.UpdateAsync().ConfigureAwait(false);
                var react = msg.Reactions.FirstOrDefault(x => x.Value.ReactionCount > 1 && x.Value.IsMe);
                if (react.Key == default)
                    continue;

                if (react.Key.Name == reaction.Name)
                {
                    var reactUsers = await msg.GetReactionUsersAsync(reaction, 100).FlattenAsync().ConfigureAwait(false);
                    var usr = reactUsers.FirstOrDefault(x => x.Id == ctx.User.Id && !x.IsBot);
                    if (usr == default)
                        continue;

                    await msg.AddReactionAsync(new Emoji("✅")).ConfigureAwait(false);
                    return false;
                }
            }
            await msg.AddReactionAsync(new Emoji("❌")).ConfigureAwait(false);
            return true;
        }

        public async Task<int> EventVoteCalc(SocketCommandContext ctx, List<PokeEventType> events)
        {
            IEmote[] reactions = { new Emoji("1️⃣"), new Emoji("2️⃣"), new Emoji("3️⃣"), new Emoji("4️⃣"), new Emoji("5️⃣") };
            string text = "The community vote has started! You have 30 seconds to vote for the next event!\n";
            for (int i = 0; i < events.Count; i++)
                text += $"{i + 1}. {events[i]}\n";

            var embed = new EmbedBuilder { Color = Color.DarkBlue }.AddField(x =>
            {
                x.Name = "Community Event Vote";
                x.Value = text;
                x.IsInline = false;
            });

            var msg = await ctx.Message.Channel.SendMessageAsync(embed: embed.Build()).ConfigureAwait(false);
            await msg.AddReactionsAsync(reactions).ConfigureAwait(false);

            await Task.Delay(30_000).ConfigureAwait(false);
            await msg.UpdateAsync().ConfigureAwait(false);

            var reactArr = msg.Reactions.Where(x => reactions.Contains(x.Key)).Select(x => x.Value.ReactionCount).ToList();
            var topVote = reactArr.Max();
            bool tieBreak = reactArr.FindAll(x => x == topVote).Count > 1;
            if (tieBreak)
            {
                List<int> indexes = new();
                for (int i = 0; i < reactArr.Count; i++)
                {
                    if (reactArr[i] == topVote)
                        indexes.Add(i);
                }
                return indexes[new Random().Next(indexes.Count)];
            }
            return reactArr.IndexOf(topVote);
        }

        public async Task EmbedUtil(SocketCommandContext ctx, string name, string value, EmbedBuilder? embed = null)
        {
            if (embed == null)
                embed = new EmbedBuilder { Color = GetBorderColor(false) };

            var splitName = name.Split(new string[] { "&^&" }, StringSplitOptions.None);
            var splitValue = value.Split(new string[] { "&^&" }, StringSplitOptions.None);
            for (int i = 0; i < splitName.Length; i++)
            {
                embed.AddField(x =>
                {
                    x.Name = splitName[i];
                    x.Value = splitValue[i];
                    x.IsInline = false;
                });
            }
            await ctx.Message.Channel.SendMessageAsync(embed: embed.Build()).ConfigureAwait(false);
        }

        public static Task ButtonExecuted(SocketMessageComponent component)
        {
            _ = Task.Run(async () =>
            {
                var id = component.Data.CustomId;
                if (id.Contains("etumrep") && !component.HasResponded)
                {
                    try
                    {
                        await component.DeferAsync().ConfigureAwait(false);
                        await EtumrepUtil.HandleEtumrepRequestAsync(component, id).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var msg = $"{ex.Message}\n{ex.StackTrace}\n{ex.InnerException}";
                        Base.LogUtil.LogError(msg, "[ButtonExecuted Event]");
                    }
                }
                else if (id.Contains("permute") && !component.HasResponded)
                    await PermuteUtil.HandlePermuteRequestAsync(component, id).ConfigureAwait(false);
            });

            return Task.CompletedTask;
        }

        public static Task SelectMenuExecuted(SocketMessageComponent component)
        {
            _ = Task.Run(async () =>
            {
                var id = component.Data.CustomId;
                if (id is "permute_json_filter" && !component.HasResponded)
                {
                    await component.DeferAsync().ConfigureAwait(false);
                    await PermuteUtil.GetPermuteFilterAsync(component).ConfigureAwait(false);
                }
            });

            return Task.CompletedTask;
        }

        public static Task ModalSubmitted(SocketModal modal)
        {
            _ = Task.Run(async () =>
            {
                await modal.DeferAsync().ConfigureAwait(false);
                var id = modal.Data.CustomId;
                if (id.Contains("permute_json"))
                    await PermuteUtil.VerifyAndRunPermuteAsync(modal).ConfigureAwait(false);
            });

            return Task.CompletedTask;
        }

        private static List<string> SpliceAtWord(string entry, int start, int length)
        {
            int counter = 0;
            List<string> list = new();
            var temp = entry.Contains(',') ? entry.Split(',').Skip(start) : entry.Contains('|') ? entry.Split('|').Skip(start) : entry.Split('\n').Skip(start);

            if (entry.Length < length)
            {
                list.Add(entry ?? "");
                return list;
            }

            foreach (var line in temp)
            {
                counter += line.Length + 2;
                if (counter < length)
                    list.Add(line.Trim());
                else break;
            }
            return list;
        }

        private static List<string> ListUtilPrep(string entry)
        {
            List<string> pageContent = new();
            if (entry.Length > 1024)
            {
                var index = 0;
                while (true)
                {
                    var splice = SpliceAtWord(entry, index, 1024);
                    if (splice.Count == 0)
                        break;

                    index += splice.Count;
                    pageContent.Add(string.Join(entry.Contains(',') ? ", " : entry.Contains('|') ? " | " : "\n", splice));
                }
            }
            else pageContent.Add(entry == "" ? "No results found." : entry);
            return pageContent;
        }

        public Color GetBorderColor(bool gift, PKM? pkm = null)
        {
            bool swsh = typeof(T) == typeof(PK8);
            if (pkm is null && swsh)
                return gift ? Color.Purple : Color.Blue;
            else if (pkm is null && !swsh)
                return gift ? Color.DarkPurple : Color.DarkBlue;
            else if (pkm is not null && swsh)
                return (pkm.IsShiny && pkm.FatefulEncounter) || pkm.ShinyXor == 0 ? Color.Gold : pkm.IsShiny ? Color.LightOrange : Color.Teal;
            else if (pkm is not null && !swsh)
                return (pkm.IsShiny && pkm.FatefulEncounter) || pkm.ShinyXor == 0 ? Color.Gold : pkm.IsShiny ? Color.DarkOrange : Color.DarkTeal;
            throw new NotImplementedException();
        }
    }
}
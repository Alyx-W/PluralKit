﻿using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NodaTime;
using NodaTime.Text;

namespace PluralKit.Core {
    public readonly struct MemberId: INumericId<MemberId, int>
    {
        public int Value { get; }

        public MemberId(int value)
        {
            Value = value;
        }

        public bool Equals(MemberId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is MemberId other && Equals(other);

        public override int GetHashCode() => Value;

        public static bool operator ==(MemberId left, MemberId right) => left.Equals(right);

        public static bool operator !=(MemberId left, MemberId right) => !left.Equals(right);

        public int CompareTo(MemberId other) => Value.CompareTo(other.Value);
        
        public override string ToString() => $"Member #{Value}";
    }

    public class PKMember
    {
        // Dapper *can* figure out mapping to getter-only properties, but this doesn't work
        // when trying to map to *subclasses* (eg. ListedMember). Adding private setters makes it work anyway.
        public MemberId Id { get; private set; }
        public string Hid { get; private set; }
        public SystemId System { get; private set; }
        public string Color { get; private set; }
        public string AvatarUrl { get; private set; }
        public string BannerImage { get; private set; }
        public string Name { get; private set; }
        public string DisplayName { get; private set; }
        public LocalDate? Birthday { get; private set; }
        public string Pronouns { get; private set; }
        public string Description { get; private set; }
        public ICollection<ProxyTag> ProxyTags { get; private set; }
        public bool KeepProxy { get; private set; }
        public Instant Created { get; private set; }
        public int MessageCount { get; private set; }
        public bool AllowAutoproxy { get; private set; }

        public PrivacyLevel MemberVisibility { get; private set; }
        public PrivacyLevel DescriptionPrivacy { get; private set; }
        public PrivacyLevel AvatarPrivacy { get; private set; }
        public PrivacyLevel NamePrivacy { get; private set; } //ignore setting if no display name is set
        public PrivacyLevel BirthdayPrivacy { get; private set; }
        public PrivacyLevel PronounPrivacy { get; private set; }
        public PrivacyLevel MetadataPrivacy { get; private set; }
        // public PrivacyLevel ColorPrivacy { get; private set; }
        
        /// Returns a formatted string representing the member's birthday, taking into account that a year of "0001" or "0004" is hidden
        /// Before Feb 10 2020, the sentinel year was 0001, now it is 0004.
        [JsonIgnore] public string BirthdayString
        {
            get
            {
                if (Birthday == null) return null;

                var format = LocalDatePattern.CreateWithInvariantCulture("MMM dd, yyyy");
                if (Birthday?.Year == 1 || Birthday?.Year == 4) format = LocalDatePattern.CreateWithInvariantCulture("MMM dd");
                return format.Format(Birthday.Value);
            }
        }

        [JsonIgnore] public bool HasProxyTags => ProxyTags.Count > 0;
    }

    public static class PKMemberExt
    {
        public static string NameFor(this PKMember member, LookupContext ctx) =>
            member.NamePrivacy.Get(ctx, member.Name, member.DisplayName ?? member.Name);

        public static string AvatarFor(this PKMember member, LookupContext ctx) =>
            member.AvatarPrivacy.Get(ctx, member.AvatarUrl.TryGetCleanCdnUrl());

        public static string DescriptionFor(this PKMember member, LookupContext ctx) =>
            member.DescriptionPrivacy.Get(ctx, member.Description);

        public static LocalDate? BirthdayFor(this PKMember member, LookupContext ctx) =>
            member.BirthdayPrivacy.Get(ctx, member.Birthday);

        public static string PronounsFor(this PKMember member, LookupContext ctx) =>
            member.PronounPrivacy.Get(ctx, member.Pronouns);

        public static Instant? CreatedFor(this PKMember member, LookupContext ctx) =>
            member.MetadataPrivacy.Get(ctx, (Instant?) member.Created);

        public static int MessageCountFor(this PKMember member, LookupContext ctx) =>
            member.MetadataPrivacy.Get(ctx, member.MessageCount);

        public static JObject ToJson(this PKMember member, LookupContext ctx)
        {
            var includePrivacy = ctx == LookupContext.ByOwner;
            
            var o = new JObject();
            o.Add("id", member.Hid);
            o.Add("name", member.NameFor(ctx));
            // o.Add("color", member.ColorPrivacy.CanAccess(ctx) ? member.Color : null);
            o.Add("color", member.Color);
            o.Add("display_name", member.NamePrivacy.CanAccess(ctx) ? member.DisplayName : null);
            o.Add("birthday", member.BirthdayFor(ctx)?.FormatExport());
            o.Add("pronouns", member.PronounsFor(ctx));
            o.Add("avatar_url", member.AvatarFor(ctx).TryGetCleanCdnUrl());
            o.Add("banner", member.DescriptionPrivacy.Get(ctx, member.BannerImage).TryGetCleanCdnUrl());
            o.Add("description", member.DescriptionFor(ctx));
            
            var tagArray = new JArray();
            foreach (var tag in member.ProxyTags) 
                tagArray.Add(new JObject {{"prefix", tag.Prefix}, {"suffix", tag.Suffix}});
            o.Add("proxy_tags", tagArray);

            o.Add("keep_proxy", member.KeepProxy);
            
            o.Add("privacy", includePrivacy ? (member.MemberVisibility.LevelName()) : null);

            o.Add("visibility", includePrivacy ? (member.MemberVisibility.LevelName()) : null);
            o.Add("name_privacy", includePrivacy ? (member.NamePrivacy.LevelName()) : null);
            o.Add("description_privacy", includePrivacy ? (member.DescriptionPrivacy.LevelName()) : null);
            o.Add("birthday_privacy", includePrivacy ? (member.BirthdayPrivacy.LevelName()) : null);
            o.Add("pronoun_privacy", includePrivacy ? (member.PronounPrivacy.LevelName()) : null);
            o.Add("avatar_privacy", includePrivacy ? (member.AvatarPrivacy.LevelName()) : null);
            // o.Add("color_privacy", ctx == LookupContext.ByOwner ? (member.ColorPrivacy.LevelName()) : null);
            o.Add("metadata_privacy", includePrivacy ? (member.MetadataPrivacy.LevelName()) : null);

            o.Add("created", member.CreatedFor(ctx)?.FormatExport());

            if (member.ProxyTags.Count > 0)
            {
                // Legacy compatibility only, TODO: remove at some point
                o.Add("prefix", member.ProxyTags?.FirstOrDefault().Prefix);
                o.Add("suffix", member.ProxyTags?.FirstOrDefault().Suffix);
            }

            return o;
        }
    }
}
using Archive.Ai.Sessions;

namespace Archive.Ai.Tests;

/// <summary>
/// §6.2: most of an archive is logistics, and the point of this is to not pay a model to read it.
/// </summary>
public sealed class SessionFilterTests
{
    [Fact]
    public void Pure_logistics_is_not_worth_a_model_call()
    {
        Assert.False(SessionFilter.IsSubstantive(
            ["on my way", "ok", "👍", "5 min", "here"]));
    }

    [Fact]
    public void A_session_of_nothing_at_all_is_not_substantive()
    {
        Assert.False(SessionFilter.IsSubstantive([]));
        Assert.False(SessionFilter.IsSubstantive(["", "   ", ""]));
    }

    [Fact]
    public void One_long_message_carries_a_session_on_its_own()
    {
        Assert.True(SessionFilter.IsSubstantive([
            "ok",
            "I finally handed in my notice today, three years to the week after starting.",
        ]));
    }

    /// <summary>
    /// A question is the cheapest signal that an exchange was about something.
    /// </summary>
    /// <remarks>
    /// "how did it go with your mother?" is short, is nothing but logistics by every length
    /// measure, and is the most useful line in a week.
    /// </remarks>
    [Fact]
    public void A_short_exchange_that_asks_something_survives()
    {
        Assert.True(SessionFilter.IsSubstantive([
            "how did it go with your mother?",
            "better than expected honestly",
        ]));
    }

    /// <summary>
    /// Half of a real archive here is Cyrillic.
    /// </summary>
    /// <remarks>
    /// An ASCII word tokenizer counts zero words in all of it and files a decade of Russian
    /// correspondence as logistics — silently, and only visible as an oddly small token bill.
    /// </remarks>
    [Fact]
    public void Cyrillic_is_words_like_any_other()
    {
        Assert.True(SessionFilter.IsSubstantive([
            "мы вчера долго говорили про переезд и про то, что будет с квартирой",
        ]));

        Assert.False(SessionFilter.IsSubstantive(["ок", "ага", "давай"]));
    }

    [Fact]
    public void Repetition_is_not_content()
    {
        // The same four words twenty times: long in total, and says one thing.
        var texts = Enumerable.Repeat("ok see you there", 20).ToArray();

        Assert.False(SessionFilter.IsSubstantive(texts));
    }

    /// <summary>
    /// A long run of logistics is still logistics.
    /// </summary>
    /// <remarks>
    /// The case a flat distinct-word threshold gets wrong, and the reason there is a per-message
    /// one as well: forty short exchanges clear any fixed word count simply by being forty of
    /// them, so the longer a logistics session runs the more certainly it is called substantive.
    /// </remarks>
    [Fact]
    public void A_long_run_of_short_logistics_is_still_logistics()
    {
        string[] chatter =
        [
            "on my way", "ok", "5 min", "here", "coming", "sorry", "no worries", "see you",
            "leaving now", "at the door", "yep", "same", "later", "cool", "sure thing",
        ];

        // Thirty messages, plenty of distinct words between them, and nothing said.
        var texts = Enumerable.Range(0, 30).Select(i => chatter[i % chatter.Length]).ToArray();

        Assert.False(SessionFilter.IsSubstantive(texts));
    }

    /// <summary>
    /// Variety, not volume: the same words said in a few messages rather than twenty.
    /// </summary>
    [Fact]
    public void A_few_messages_that_say_a_lot_are_enough()
    {
        Assert.True(SessionFilter.IsSubstantive([
            "we should book flights",
            "before prices climb again",
            "like last autumn when everything doubled",
            "overnight and nobody warned anyone at all",
        ]));

        // The same words, one per message. Twenty turns of one word each is not a conversation
        // about anything, whatever the vocabulary adds up to.
        Assert.False(SessionFilter.IsSubstantive([
            "we", "should", "book", "the", "flights", "before", "prices", "climb",
            "again", "like", "last", "autumn", "when", "everything", "doubled",
            "overnight", "and", "nobody", "warned", "anyone", "at", "all",
        ]));
    }
}

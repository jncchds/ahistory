using Archive.Ai.Extraction;

namespace Archive.Ai.Tests;

/// <summary>
/// What a tool call is allowed to become.
/// </summary>
/// <remarks>
/// The rejection messages matter as much as the rejections: they go straight back to the model as
/// the tool's result, and are the only chance it gets to fix the call.
/// </remarks>
public sealed class ToolDispatcherTests
{
    private static ExtractionWindow Window(bool group = false) => new(
        SessionId: "s1",
        ThreadId: "t1",
        IsGroup: group,
        People:
        [
            new WindowPerson("p_them", "Sam", false),
            new WindowPerson("p_me", "You", true),
        ],
        Messages:
        [
            new WindowMessage(10, "p_them", "Sam", 1_700_000_000, "I started at Acme last week"),
            new WindowMessage(11, "p_me", "You", 1_700_000_060, "congratulations!"),
        ],
        Known: [new KnownFact("f_old", "p_them", "lives_in", "Berlin", "Sam lives in Berlin.")]);

    private const string GoodFact = """
        {"subject_person":"p_them","predicate":"works_at","object":"Acme",
         "claim":"Sam started at Acme.","confidence":0.9,
         "message_ids":[10],"quote":"I started at Acme"}
        """;

    [Fact]
    public void A_well_formed_fact_is_staged()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.True(dispatcher.Dispatch(FactTools.RecordFact, GoodFact).Accepted);

        var fact = Assert.Single(dispatcher.Facts);

        Assert.Equal("p_them", fact.SubjectPersonId);
        Assert.Equal("works_at", fact.Predicate);
        Assert.Equal(0.9, fact.Confidence);
        Assert.Equal("dm", fact.OriginKind);
    }

    /// <summary>
    /// A cited message from outside the window never reaches the store.
    /// </summary>
    /// <remarks>
    /// The mechanical half of citation enforcement, and the only half there is: whether a sentence
    /// follows from a message cannot be checked, but whether the message is even in the
    /// conversation can, and a hallucinated id is the commonest way for it not to be.
    /// </remarks>
    [Fact]
    public void A_citation_from_outside_the_window_is_refused_by_id()
    {
        var dispatcher = new ToolDispatcher(Window());

        var reply = dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","predicate":"works_at","object":"Acme",
             "claim":"Sam started at Acme.","confidence":0.9,
             "message_ids":[9999]}
            """);

        Assert.False(reply.Accepted);
        Assert.Contains("9999", reply.Message, StringComparison.Ordinal);
        Assert.Empty(dispatcher.Facts);
    }

    /// <summary>§8: a claim with nothing behind it is dropped rather than shown.</summary>
    [Fact]
    public void A_fact_with_no_citations_is_refused()
    {
        var dispatcher = new ToolDispatcher(Window());

        var reply = dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","predicate":"works_at","object":"Acme",
             "claim":"Sam started at Acme.","confidence":0.9,"message_ids":[]}
            """);

        Assert.False(reply.Accepted);
        Assert.Empty(dispatcher.Facts);
    }

    [Fact]
    public void A_subject_who_is_not_here_is_refused()
    {
        var dispatcher = new ToolDispatcher(Window());

        var reply = dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_stranger","predicate":"works_at","object":"Acme",
             "claim":"x","confidence":0.9,"message_ids":[10]}
            """);

        Assert.False(reply.Accepted);
        Assert.Contains("p_stranger", reply.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fact needs a subject, and a relationship needs two different people.
    /// </summary>
    /// <remarks>
    /// The flat shape cannot express "both a person and a pair" — which is the point of it. What
    /// is left to check is a missing subject and a pair of one person with themselves.
    /// </remarks>
    [Fact]
    public void A_fact_needs_a_subject_and_a_pair_needs_two_of_them()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.False(dispatcher.Dispatch(FactTools.RecordFact, """
            {"predicate":"met_in","object":"Berlin","claim":"x","confidence":0.9,
             "message_ids":[10]}
            """).Accepted);

        Assert.False(dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","subject_person_b":"p_them","predicate":"met_in",
             "object":"Berlin","claim":"x","confidence":0.9,"message_ids":[10]}
            """).Accepted);

        Assert.False(dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","subject_person_b":"p_nobody","predicate":"met_in",
             "object":"Berlin","claim":"x","confidence":0.9,"message_ids":[10]}
            """).Accepted);

        Assert.Empty(dispatcher.Facts);
    }

    /// <summary>
    /// A pair is stored in one order, so an unordered pair cannot exist twice.
    /// </summary>
    [Fact]
    public void A_pair_is_put_in_canonical_order()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.True(dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","subject_person_b":"p_me","predicate":"met_in","object":"Berlin",
             "claim":"They met in Berlin.","confidence":0.7,"message_ids":[10]}
            """).Accepted);

        var fact = Assert.Single(dispatcher.Facts);

        Assert.Equal(("p_me", "p_them"), fact.SubjectPair);
        Assert.Null(fact.SubjectPersonId);
    }

    [Fact]
    public void A_predicate_that_is_not_a_key_is_refused()
    {
        var dispatcher = new ToolDispatcher(Window());

        var reply = dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","predicate":"Works At!","object":"Acme","claim":"x",
             "confidence":0.9,"message_ids":[10]}
            """);

        Assert.False(reply.Accepted);
        Assert.Contains("works_at", reply.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Confidence_outside_zero_to_one_is_refused()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.False(dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","predicate":"works_at","object":"Acme","claim":"x",
             "confidence":4,"message_ids":[10]}
            """).Accepted);
    }

    /// <summary>
    /// Who said it decides whether a claim is self-reported or reflected — not the model.
    /// </summary>
    /// <remarks>
    /// §7: reflected evidence captures what people never say about themselves and is the easiest
    /// to get wrong, so it has to be distinguishable later. Asking the model would add a field it
    /// can be confidently wrong about, when the transcript already answers it.
    /// </remarks>
    [Fact]
    public void Evidence_kind_follows_from_who_said_it()
    {
        var dispatcher = new ToolDispatcher(Window());

        dispatcher.Dispatch(FactTools.RecordFact, GoodFact);

        Assert.Equal("self_report", dispatcher.Facts[0].EvidenceKind);

        // The same claim, cited to the other person's message: someone else saying it about them.
        dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","predicate":"works_at","object":"Acme",
             "claim":"Sam started at Acme.","confidence":0.5,"message_ids":[11]}
            """);

        Assert.Equal("reflected", dispatcher.Facts[1].EvidenceKind);
    }

    /// <summary>§4: the same sentence is weaker evidence said to eleven people.</summary>
    [Fact]
    public void A_group_conversation_marks_its_facts_as_group()
    {
        var dispatcher = new ToolDispatcher(Window(group: true));

        dispatcher.Dispatch(FactTools.RecordFact, GoodFact);

        Assert.Equal("group", dispatcher.Facts[0].OriginKind);
    }

    [Fact]
    public void A_validity_date_brings_the_message_that_establishes_it()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.True(dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","predicate":"works_at","object":"Acme",
             "claim":"Sam started at Acme.","confidence":0.9,
             "message_ids":[10],
             "valid_from":"2023-11-10","valid_from_message_id":10}
            """).Accepted);

        var fact = dispatcher.Facts[0];

        Assert.StartsWith("2023-11-10", fact.ValidFromUtc!, StringComparison.Ordinal);
        Assert.Contains(fact.Citations, c => c.Role == "establishes_valid_from" && c.MessageId == 10);
    }

    /// <summary>
    /// A date far enough in the future is a misread year, not a plan.
    /// </summary>
    [Fact]
    public void An_implausible_future_date_is_refused()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.False(dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","predicate":"works_at","object":"Acme","claim":"x",
             "confidence":0.9,"message_ids":[10],
             "valid_from":"2190-01-01","valid_from_message_id":10}
            """).Accepted);
    }

    [Fact]
    public void Corroborating_and_contradicting_need_a_fact_that_exists()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.True(dispatcher.Dispatch(FactTools.CorroborateFact, """
            {"fact_id":"f_old","message_ids":[10]}
            """).Accepted);

        Assert.False(dispatcher.Dispatch(FactTools.ContradictFact, """
            {"fact_id":"f_missing","message_ids":[10]}
            """).Accepted);

        Assert.Single(dispatcher.Corroborations);
        Assert.Empty(dispatcher.Contradictions);
    }

    /// <summary>
    /// Superseding takes the subject and predicate from the fact it replaces.
    /// </summary>
    /// <remarks>
    /// Not from the model: a replacement that drifted to a different predicate would leave the old
    /// fact closed out by something that is not the same claim about the same thing.
    /// </remarks>
    [Fact]
    public void Superseding_keeps_the_subject_and_predicate_of_what_it_replaces()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.True(dispatcher.Dispatch(FactTools.SupersedeFact, """
            {"fact_id":"f_old","object":"Vienna","claim":"Sam has moved to Vienna.",
             "confidence":0.8,"message_ids":[10]}
            """).Accepted);

        var fact = Assert.Single(dispatcher.Facts);

        Assert.Equal("lives_in", fact.Predicate);
        Assert.Equal("p_them", fact.SubjectPersonId);
        Assert.Equal("Vienna", fact.ObjectText);
        Assert.Equal("f_old", fact.SupersedesFactId);
    }

    /// <summary>
    /// The explicit exit, and the reason it exists.
    /// </summary>
    /// <remarks>
    /// Most sessions are logistics. Without a way to say so, a model that believes empty-handed
    /// means failure fills the store with "Sam said he was on his way".
    /// </remarks>
    [Fact]
    public void Nothing_to_record_is_an_answer()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.True(dispatcher.Dispatch(FactTools.NothingToRecord, """{"reason":"arrangements"}""").Accepted);
        Assert.True(dispatcher.NothingToRecord);
        Assert.Equal("arrangements", dispatcher.NothingReason);
    }

    [Fact]
    public void A_tool_that_does_not_exist_and_arguments_that_are_not_json_are_refused()
    {
        var dispatcher = new ToolDispatcher(Window());

        Assert.False(dispatcher.Dispatch("delete_everything", "{}").Accepted);
        Assert.False(dispatcher.Dispatch(FactTools.RecordFact, "{not json").Accepted);
    }

    /// <summary>
    /// A value that only repeats the key is not a fact.
    /// </summary>
    /// <remarks>
    /// Found on the first run against a real model: "just landed" became <c>landed = landed</c>
    /// at 0.9. The refusal says why, so the model can drop it rather than resend it.
    /// </remarks>
    [Fact]
    public void A_value_that_only_restates_the_predicate_is_refused()
    {
        var dispatcher = new ToolDispatcher(Window());

        var reply = dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","predicate":"landed","object":"landed",
             "claim":"Sam just landed.","confidence":0.9,"message_ids":[10]}
            """);

        Assert.False(reply.Accepted);
        Assert.Contains("only repeats", reply.Message, StringComparison.Ordinal);

        Assert.False(dispatcher.Dispatch(FactTools.RecordFact, """
            {"subject_person":"p_them","predicate":"has_child","object":"Child",
             "claim":"x","confidence":0.9,"message_ids":[10]}
            """).Accepted);

        Assert.False(dispatcher.Dispatch(FactTools.SupersedeFact, """
            {"fact_id":"f_old","object":"lives","claim":"x","confidence":0.8,"message_ids":[10]}
            """).Accepted);

        Assert.Empty(dispatcher.Facts);
    }
}

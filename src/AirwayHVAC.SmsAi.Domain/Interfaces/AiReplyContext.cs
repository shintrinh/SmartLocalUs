// Repo path: src/AirwayHVAC.SmsAi.Domain/Interfaces/AiReplyContext.cs
// NEW FILE — hackathon Day 3-4 (hoàn thiện calling).
// Carries the conversation state the AI needs to reply stage-appropriately.
// The orchestrator builds it AFTER the flow engine runs, so Stage already
// reflects this turn's message, and the AI is told what to ask for next
// instead of guessing — this closes the stage/AI divergence gap where the
// keyword stage machine advanced but an unguided AI reply talked about
// something else (worst at Confirm, where the static template reads back
// fields and expects yes/no).

using AirwayHVAC.SmsAi.Domain.Enums;

namespace AirwayHVAC.SmsAi.Domain.Interfaces;

public sealed class AiReplyContext
{
    public AiReplyContext(
        ConversationStage stage,
        string? issue,
        string? address,
        string? urgency,
        string? preferredTime)
    {
        Stage = stage;
        Issue = issue;
        Address = address;
        Urgency = urgency;
        PreferredTime = preferredTime;
    }

    public ConversationStage Stage { get; }

    public string? Issue { get; }

    public string? Address { get; }

    public string? Urgency { get; }

    public string? PreferredTime { get; }

    public IReadOnlyList<string> MissingFields
    {
        get
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(Issue))
            {
                missing.Add("Issue");
            }

            if (string.IsNullOrWhiteSpace(Address))
            {
                missing.Add("Address");
            }

            if (string.IsNullOrWhiteSpace(Urgency))
            {
                missing.Add("Urgency");
            }

            if (string.IsNullOrWhiteSpace(PreferredTime))
            {
                missing.Add("PreferredTime");
            }

            return missing;
        }
    }

    // One system message worth of guidance, shared by both providers so the
    // stage story is identical on Azure and Meta. Responders add it as a
    // system message; it is never stored in conversation history.
    public string BuildStageGuidance()
    {
        var known = $"Issue: {Issue ?? "(not provided)"}; " +
            $"Address: {Address ?? "(not provided)"}; " +
            $"Urgency: {Urgency ?? "(not provided)"}; " +
            $"Preferred time: {PreferredTime ?? "(not provided)"}";
        var missing = MissingFields.Count == 0 ? "none" : string.Join(", ", MissingFields);
        var instruction = Stage switch
        {
            ConversationStage.Issue => "Ask what HVAC issue the customer is having.",
            ConversationStage.Address => "Ask for the service address.",
            ConversationStage.Urgency => "Ask how urgent it is: emergency, same-day, or flexible.",
            ConversationStage.Appointment => "Ask for the preferred appointment time.",
            ConversationStage.Confirm => "Read back the issue, address, urgency and preferred time, then ask whether everything is correct (answer yes or no).",
            ConversationStage.Completed => "The booking is confirmed. Keep the reply brief and polite.",
            _ => "Continue the conversation helpfully."
        };
        return $"[Stage guidance] Current stage: {Stage}. Known details: {known}. " +
            $"Still missing: {missing}. {instruction}";
    }
}

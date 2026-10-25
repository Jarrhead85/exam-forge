using System.Security.Claims;
using ExamForge.Application.StudentFlow;
using Microsoft.AspNetCore.Authentication;

namespace ExamForge.Web.Authentication;

/// <summary>Схема студенческой сессии (ADR-002): отдельна от учительской, cookie ef.student.</summary>
public static class StudentAuth
{
    public const string Scheme = "Student";

    public const string InvitationClaim = "inv";
    public const string AssignmentClaim = "asg";
    public const string StudentProfileClaim = "stp";
    public const string GroupClaim = "grp";

    public static ClaimsPrincipal CreatePrincipal(Domain.Assignments.Invitation invitation)
    {
        var claims = new List<Claim>
        {
            new(InvitationClaim, invitation.Id.ToString()),
            new(AssignmentClaim, invitation.AssignmentId.ToString()),
        };
        if (invitation.StudentProfileId is { } profileId)
            claims.Add(new Claim(StudentProfileClaim, profileId.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    /// <summary>Сессия входа по общей ссылке (ТЗ §5.1): назначение + эфемерный профиль + группа.</summary>
    public static ClaimsPrincipal CreateCommonLinkPrincipal(Guid assignmentId, Guid profileId, string? group)
    {
        var claims = new List<Claim>
        {
            new(AssignmentClaim, assignmentId.ToString()),
            new(StudentProfileClaim, profileId.ToString()),
        };
        if (!string.IsNullOrWhiteSpace(group))
            claims.Add(new Claim(GroupClaim, group.Trim()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    /// <summary>Сессия постоянного аккаунта (ТЗ §5.3): без привязки к одному назначению.</summary>
    public static ClaimsPrincipal CreateAccountPrincipal(Domain.People.StudentAccount account)
        => new(new ClaimsIdentity(
            [new Claim(StudentProfileClaim, account.StudentProfileId.ToString())],
            Scheme));

    /// <summary>Идентичность студента из claims; null, если claims неполные.</summary>
    public static StudentIdentity? StudentIdentity(this ClaimsPrincipal user)
    {
        var profile = Guid.TryParse(user.FindFirstValue(StudentProfileClaim), out var profileId) ? profileId : (Guid?)null;
        var assignment = Guid.TryParse(user.FindFirstValue(AssignmentClaim), out var assignmentId) ? assignmentId : (Guid?)null;
        var invitation = Guid.TryParse(user.FindFirstValue(InvitationClaim), out var invitationId) ? invitationId : (Guid?)null;
        if (profile is null && assignment is null)
            return null;
        var group = user.FindFirstValue(GroupClaim);
        return new StudentIdentity(invitation, assignment, profile, group);
    }

    public static Task SignInStudentAsync(this HttpContext context, string scheme, ClaimsPrincipal principal)
        => context.SignInAsync(scheme, principal);
}

using System.Security.Claims;
using ExamForge.Domain.Assignments;
using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Web.Authentication;

public sealed class StudentCookieEvents(ExamForgeDbContext db, TimeProvider timeProvider) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var identity = principal?.StudentIdentity();
        var ct = context.HttpContext.RequestAborted;
        var now = timeProvider.GetUtcNow();
        var valid = false;

        if (principal is not null && identity?.StudentProfileId is { } profileId)
        {
            if (identity.InvitationId is { } invitationId)
            {
                valid = await db.Invitations.AsNoTracking().AnyAsync(invitation =>
                    invitation.Id == invitationId && invitation.AssignmentId == identity.AssignmentId &&
                    invitation.StudentProfileId == profileId && invitation.Status == InvitationStatus.Active &&
                    (invitation.ExpiresAtUtc == null || invitation.ExpiresAtUtc > now), ct);
            }
            else if (identity.AssignmentId is { } assignmentId)
            {
                var link = principal.FindFirstValue(StudentAuth.LinkClaim);
                valid = link is not null && await db.Assignments.AsNoTracking().AnyAsync(assignment =>
                    assignment.Id == assignmentId && assignment.AllowCommonLink && assignment.CommonLinkCode == link, ct);
            }
            else if (Guid.TryParse(principal.FindFirstValue(StudentAuth.AccountClaim), out var accountId) &&
                     Guid.TryParse(principal.FindFirstValue(StudentAuth.SessionVersionClaim), out var version))
            {
                valid = await db.StudentAccounts.AsNoTracking().AnyAsync(account =>
                    account.Id == accountId && account.StudentProfileId == profileId && !account.IsDisabled &&
                    account.SessionVersion == version &&
                    (account.LockedUntilUtc == null || account.LockedUntilUtc <= now), ct);
            }
        }

        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(StudentAuth.Scheme);
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
        => Unauthorized(context);

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
        => Unauthorized(context);

    private static Task Unauthorized(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

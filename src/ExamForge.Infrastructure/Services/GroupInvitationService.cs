using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Access;
using ExamForge.Domain.Assignments;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record GroupInvitationResult(int AlreadyInvited, IReadOnlyList<InvitationDto> Issued);

public sealed class GroupInvitationService(
    IDbContextFactory<ExamForgeDbContext> contextFactory, GroupService groups,
    InvitationCodeHasher hasher, TimeProvider timeProvider)
{
    public async Task<GroupInvitationResult> IssueAsync(
        TeacherActor actor, Guid assignmentId, Guid groupId, CancellationToken ct)
    {
        var members = await groups.MembersAsync(actor, groupId, ct);
        var ids = members.Select(member => member.StudentProfileId).ToArray();
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var assignment = await db.Assignments
                .FromSql($"""SELECT * FROM assignments WHERE "Id" = {assignmentId} FOR UPDATE""")
                .AsNoTracking()
                .SingleOrDefaultAsync(item => actor.IsAdministrator || item.CreatedByTeacherId == actor.TeacherId, ct)
                ?? throw TeacherFlowErrors.NotFound("Назначение не найдено.");

            var groupName = await db.Groups.AsNoTracking().Where(group => group.Id == groupId)
                .Select(group => group.Name).SingleAsync(ct);
            var existing = await db.Invitations.AsNoTracking()
                .Where(invitation => invitation.AssignmentId == assignment.Id && invitation.Status == InvitationStatus.Active &&
                    invitation.StudentProfileId != null && ids.Contains(invitation.StudentProfileId.Value))
                .Select(invitation => invitation.StudentProfileId!.Value).Distinct().ToListAsync(ct);
            var alreadyInvited = existing.ToHashSet();
            var issued = new List<InvitationDto>();
            var now = timeProvider.GetUtcNow();

            foreach (var member in members.Where(member => !alreadyInvited.Contains(member.StudentProfileId)))
            {
                var code = InvitationCode.Generate();
                var invitation = Invitation.Create(assignment.Id, member.StudentProfileId,
                    hasher.ComputeHash(InvitationCode.Normalize(code)), null, actor.TeacherId, now, groupName);
                db.Invitations.Add(invitation);
                issued.Add(new InvitationDto(invitation.Id, member.StudentProfileId, member.Name, groupName,
                    now, invitation.Status.ToString(), null, code));
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new GroupInvitationResult(alreadyInvited.Count, issued);
        });
    }
}

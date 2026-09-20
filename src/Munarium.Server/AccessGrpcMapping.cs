namespace Munarium.Server;

using Munarium.Wire;
using Munarium.Wire.Generated;

/// <summary>Reads the issuance audit's wire shapes as the messages the specification declares.</summary>
internal static class AccessGrpcMapping
{
    /// <summary>Reads a tenant's audit as the list message.</summary>
    /// <param name="audit">The audit.</param>
    /// <returns>The message.</returns>
    internal static ListAccessTokensResponse ToMessage(WireAccessTokenAuditList audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        var list = new AccessTokenAuditList();
        list.Capabilities.AddRange(audit.Capabilities.Select(ToMessage));

        return new ListAccessTokensResponse { Data = list };
    }

    /// <summary>Reads one audited capability as the message.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The message.</returns>
    internal static AccessTokenAudit ToMessage(WireAccessTokenAudit row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var message = new AccessTokenAudit
        {
            TokenId = row.TokenId,
            Subject = row.Subject,
            Tenant = row.Tenant,
            Level = row.Level,
            IssuedAt = row.IssuedAt,
            ExpiresAt = row.ExpiresAt,
        };

        message.Compartments.AddRange(row.Compartments);
        message.Scopes.AddRange(row.Scopes);

        if (row.Runbooks is { } runbooks)
        {
            message.Runbooks.AddRange(runbooks);
        }

        if (row.RevokedAt is { } withdrawn)
        {
            message.RevokedAt = withdrawn;
        }

        return message;
    }
}

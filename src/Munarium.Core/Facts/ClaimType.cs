namespace Munarium.Facts;

/// <summary>
/// What a claim does to whatever the ledger already holds on its lineage.
/// </summary>
/// <remarks>
/// This is the difference between a correction and a silent overwrite, so it is part of the fact rather
/// than metadata about it: the ledger-conflict gate reads it to decide whether superseding is intended.
/// </remarks>
public enum ClaimType
{
    /// <summary>No type was given; treated as a new sub-lineage that nothing needs to be told about.</summary>
    Unspecified = 0,

    /// <summary>A value that the ledger did not already hold. Superseding without saying so is a conflict.</summary>
    Fact = 1,

    /// <summary>The value legitimately changed - a status transition. Superseding is intended.</summary>
    Update = 2,

    /// <summary>The earlier value was wrong. Superseding is a correction.</summary>
    Correction = 3,
}

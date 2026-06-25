using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ITCDI.Core.Enums
{
    public enum MappingType
    {
        OneToOne,
        Concatenation,
        Split,
        Derived,
        Conditional,
        Aggregation
    }

    public enum DriftType
    {
        ColumnRename,
        ColumnAdd,
        ColumnRemove,
        TypeWidening,
        TypeNarrowing,
        TableSplit,
        TableMerge,
        ValueDistributionDrift
    }

    public enum ImpactLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    public enum ConflictType
    {
        PrimaryKeyCollision,
        ValueConflict,
        ReferentialIntegrity,
        ConstraintViolation
    }

    public enum ResolutionPolicy
    {
        LastWriteWins,
        SourcePriority,
        TimestampBased,
        ProvenancePreserved,
        Rejected
    }

    public enum IntegrationStatus
    {
        Running,
        Success,
        Failed,
        Partial
    }

    public enum TriggerSource
    {
        Api,
        Scheduler
    }

    public enum RunMode
    {
        Incremental,
        FullReload
    }
}

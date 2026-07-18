@{
    Version = 3
    SourceRoot = 'Assets/Scripts'
    GlobalNamespaceBaseline = 'Tools/GlobalNamespaceBaseline.txt'
    Rules = @(
        @{
            Path = 'Assets/Scripts/Core'
            Assembly = 'AnimarsCatcher.Core'
            AsmdefPath = 'Assets/Scripts/Core/AnimarsCatcher.Core.asmdef'
            RootNamespace = 'AnimarsCatcher.Core'
            Owner = 'Core'
            Status = 'PhaseTwoImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Core')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @()
        }
        @{
            Path = 'Assets/Scripts/Gameplay/Contracts'
            Assembly = 'AnimarsCatcher.Gameplay.Contracts'
            AsmdefPath = 'Assets/Scripts/Gameplay/Contracts/AnimarsCatcher.Gameplay.Contracts.asmdef'
            RootNamespace = 'AnimarsCatcher.Gameplay.Contracts'
            Owner = 'Gameplay Contracts'
            Status = 'PhaseTwoImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Gameplay.Contracts')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @('AnimarsCatcher.Core')
        }
        @{
            Path = 'Assets/Scripts/Anis/Navigation/Grid'
            Assembly = 'AnimarsCatcher.Navigation'
            AsmdefPath = 'Assets/Scripts/Anis/Navigation/Grid/AnimarsCatcher.Navigation.asmdef'
            RootNamespace = 'AnimarsCatcher.Animars.Navigation'
            Owner = 'Navigation'
            Status = 'PhaseOneImplemented'
            Lifecycle = 'Mixed'
            NamespacePrefixes = @('AnimarsCatcher.Animars.Navigation')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @(
                'AnimarsCatcher.Core',
                'AnimarsCatcher.Gameplay.Contracts'
            )
        }
        @{
            Path = 'Assets/Scripts/Anis'
            Assembly = 'AnimarsCatcher.Animars'
            Owner = 'Ani Gameplay'
            Status = 'PendingContracts'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Animars')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Base'
            Assembly = 'AnimarsCatcher.Base'
            Owner = 'Base Gameplay'
            Status = 'PendingDependencyAudit'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Base')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Benchmarks'
            Assembly = 'AnimarsCatcher.Benchmarks.LegacyNavigation'
            Owner = 'Navigation Benchmark'
            Status = 'PendingRuntimeIsolation'
            Lifecycle = 'Benchmark'
            NamespacePrefixes = @('AnimarsCatcher.Benchmarks')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Camp'
            Assembly = 'AnimarsCatcher.Camp'
            Owner = 'Camp Gameplay'
            Status = 'PendingContracts'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Camp')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Editor'
            Assembly = 'AnimarsCatcher.Editor'
            Owner = 'Project Editor Tools'
            Status = 'PendingRuntimeDependencies'
            Lifecycle = 'Editor'
            NamespacePrefixes = @('AnimarsCatcher.Editor')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Global'
            Assembly = 'AnimarsCatcher.Global'
            Owner = 'Match Lifecycle'
            Status = 'PendingContracts'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Global')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Health'
            Assembly = 'AnimarsCatcher.Health'
            Owner = 'Health Gameplay'
            Status = 'PendingContracts'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Health')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/MonoBehaviour'
            Assembly = 'AnimarsCatcher.Mono'
            Owner = 'GameObject Presentation'
            Status = 'PendingRuntimeDependencies'
            Lifecycle = 'Presentation'
            NamespacePrefixes = @('AnimarsCatcher.Mono')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Netcode'
            Assembly = 'AnimarsCatcher.Networking'
            Owner = 'Networking'
            Status = 'PendingContracts'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Networking')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Physics'
            Assembly = 'AnimarsCatcher.Physics'
            Owner = 'Physics Authoring'
            Status = 'PendingDependencyAudit'
            Lifecycle = 'Authoring'
            NamespacePrefixes = @('AnimarsCatcher.Physics')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Player'
            Assembly = 'AnimarsCatcher.Player'
            Owner = 'Player'
            Status = 'PendingContracts'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @(
                'AnimarsCatcher.Player',
                'Unity.CharacterController.Editor'
            )
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Resource'
            Assembly = 'AnimarsCatcher.Resource'
            Owner = 'Resource Gameplay'
            Status = 'PendingContracts'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Resource')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/Terrain'
            Assembly = 'AnimarsCatcher.Terrain'
            Owner = 'Terrain Authoring'
            Status = 'PendingDependencyAudit'
            Lifecycle = 'Authoring'
            NamespacePrefixes = @('AnimarsCatcher.Terrain')
            RequireNamespace = $false
        }
        @{
            Path = 'Assets/Scripts/UI'
            Assembly = 'AnimarsCatcher.UI'
            Owner = 'ECS Presentation'
            Status = 'PendingRuntimeDependencies'
            Lifecycle = 'Presentation'
            NamespacePrefixes = @('AnimarsCatcher.UI')
            RequireNamespace = $false
        }
    )
}

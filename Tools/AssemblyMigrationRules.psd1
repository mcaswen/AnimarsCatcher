@{
    Version = 6
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
            Path = 'Assets/Scripts/Gameplay'
            Assembly = 'AnimarsCatcher.Gameplay'
            AsmdefPath = 'Assets/Scripts/Gameplay/AnimarsCatcher.Gameplay.asmdef'
            RootNamespace = 'AnimarsCatcher.Gameplay'
            Owner = 'Gameplay Runtime'
            Status = 'PhaseThreeImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Gameplay')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @(
                'AnimarsCatcher.Core',
                'AnimarsCatcher.Gameplay.Contracts'
            )
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
            Assembly = 'AnimarsCatcher.Gameplay'
            AsmrefPath = 'Assets/Scripts/Anis/AnimarsCatcher.Gameplay.asmref'
            Owner = 'Ani Gameplay'
            Status = 'PhaseThreeImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Gameplay')
            RequireNamespace = $true
        }
        @{
            Path = 'Assets/Scripts/Base'
            Assembly = 'AnimarsCatcher.Gameplay'
            AsmrefPath = 'Assets/Scripts/Base/AnimarsCatcher.Gameplay.asmref'
            Owner = 'Base Gameplay'
            Status = 'PhaseThreeImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Gameplay')
            RequireNamespace = $true
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
            Assembly = 'AnimarsCatcher.Gameplay'
            AsmrefPath = 'Assets/Scripts/Camp/AnimarsCatcher.Gameplay.asmref'
            Owner = 'Camp Gameplay'
            Status = 'PhaseThreeImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Gameplay')
            RequireNamespace = $true
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
            Assembly = 'AnimarsCatcher.Gameplay'
            AsmrefPath = 'Assets/Scripts/Global/AnimarsCatcher.Gameplay.asmref'
            Owner = 'Match Lifecycle'
            Status = 'PhaseThreeImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Gameplay')
            RequireNamespace = $true
        }
        @{
            Path = 'Assets/Scripts/Health'
            Assembly = 'AnimarsCatcher.Gameplay'
            AsmrefPath = 'Assets/Scripts/Health/AnimarsCatcher.Gameplay.asmref'
            Owner = 'Health Gameplay'
            Status = 'PhaseThreeImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Gameplay')
            RequireNamespace = $true
        }
        @{
            Path = 'Assets/Scripts/MonoBehaviour'
            Assembly = 'AnimarsCatcher.Presentation'
            Owner = 'GameObject Presentation'
            Status = 'PhaseFivePrepared'
            Lifecycle = 'Presentation'
            NamespacePrefixes = @('AnimarsCatcher.Presentation')
            RequireNamespace = $true
        }
        @{
            Path = 'Assets/Scripts/Netcode'
            Assembly = 'AnimarsCatcher.Networking'
            AsmdefPath = 'Assets/Scripts/Netcode/AnimarsCatcher.Networking.asmdef'
            RootNamespace = 'AnimarsCatcher.Networking'
            Owner = 'Networking'
            Status = 'PhaseFourImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Networking')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @(
                'AnimarsCatcher.Gameplay',
                'AnimarsCatcher.Gameplay.Contracts',
                'AnimarsCatcher.Player'
            )
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
            Path = 'Assets/Scripts/Player/Input/Editor'
            Assembly = 'AnimarsCatcher.Player.Editor'
            AsmdefPath = 'Assets/Scripts/Player/Input/Editor/AnimarsCatcher.Player.Editor.asmdef'
            RootNamespace = 'AnimarsCatcher.Player.Editor'
            Owner = 'Player Editor'
            Status = 'PhaseFourImplemented'
            Lifecycle = 'Editor'
            NamespacePrefixes = @('AnimarsCatcher.Player.Editor')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @('AnimarsCatcher.Player')
        }
        @{
            Path = 'Assets/Scripts/Player'
            Assembly = 'AnimarsCatcher.Player'
            AsmdefPath = 'Assets/Scripts/Player/AnimarsCatcher.Player.asmdef'
            RootNamespace = 'AnimarsCatcher.Player'
            Owner = 'Player'
            Status = 'PhaseFourImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Player')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @('AnimarsCatcher.Gameplay')
        }
        @{
            Path = 'Assets/Scripts/Resource'
            Assembly = 'AnimarsCatcher.Gameplay'
            AsmrefPath = 'Assets/Scripts/Resource/AnimarsCatcher.Gameplay.asmref'
            Owner = 'Resource Gameplay'
            Status = 'PhaseThreeImplemented'
            Lifecycle = 'Runtime'
            NamespacePrefixes = @('AnimarsCatcher.Gameplay')
            RequireNamespace = $true
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
            Assembly = 'AnimarsCatcher.Presentation'
            Owner = 'ECS Presentation'
            Status = 'PhaseFivePrepared'
            Lifecycle = 'Presentation'
            NamespacePrefixes = @('AnimarsCatcher.Presentation')
            RequireNamespace = $true
        }
    )
}

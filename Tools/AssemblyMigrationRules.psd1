@{
    Version = 11
    SourceRoot = 'Assets/Scripts'
    GlobalNamespaceBaseline = 'Tools/GlobalNamespaceBaseline.txt'
    ProjectAssembliesAutoReferenced = $false
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
            Path = 'Assets/Scripts/Navigation/Grid/Editor'
            Assembly = 'AnimarsCatcher.Navigation.Editor'
            AsmdefPath = 'Assets/Scripts/Navigation/Grid/Editor/AnimarsCatcher.Navigation.Editor.asmdef'
            RootNamespace = 'AnimarsCatcher.Navigation.Grid.Editor'
            Owner = 'Navigation Editor'
            Status = 'PhaseSevenImplemented'
            Lifecycle = 'Editor'
            NamespacePrefixes = @('AnimarsCatcher.Navigation.Grid.Editor')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @(
                'AnimarsCatcher.Gameplay',
                'AnimarsCatcher.Gameplay.Contracts',
                'AnimarsCatcher.Navigation'
            )
        }
        @{
            Path = 'Assets/Scripts/Navigation/Grid'
            Assembly = 'AnimarsCatcher.Navigation'
            AsmdefPath = 'Assets/Scripts/Navigation/Grid/AnimarsCatcher.Navigation.asmdef'
            RootNamespace = 'AnimarsCatcher.Navigation'
            Owner = 'Navigation'
            Status = 'PhaseSevenTightened'
            Lifecycle = 'Mixed'
            NamespacePrefixes = @('AnimarsCatcher.Navigation')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @(
                'AnimarsCatcher.Core',
                'AnimarsCatcher.Gameplay.Contracts'
            )
        }
        @{
            Path = 'Assets/Scripts/Benchmarks'
            Assembly = 'AnimarsCatcher.Benchmarks.LegacyNavigation'
            AsmdefPath = 'Assets/Scripts/Benchmarks/LegacyNavMesh/AnimarsCatcher.Benchmarks.LegacyNavigation.asmdef'
            RootNamespace = 'AnimarsCatcher.Benchmarks.LegacyNavigation'
            Owner = 'Navigation Benchmark'
            Status = 'PhaseSixImplemented'
            Lifecycle = 'Benchmark'
            NamespacePrefixes = @('AnimarsCatcher.Benchmarks')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @(
                'AnimarsCatcher.Core',
                'AnimarsCatcher.Gameplay',
                'AnimarsCatcher.Gameplay.Contracts',
                'AnimarsCatcher.Navigation',
                'AnimarsCatcher.Player'
            )
        }
        @{
            Path = 'Assets/Scripts/Editor'
            Assembly = 'AnimarsCatcher.Editor'
            AsmdefPath = 'Assets/Scripts/Editor/AnimarsCatcher.Editor.asmdef'
            RootNamespace = 'AnimarsCatcher.Editor'
            Owner = 'Project Editor Tools'
            Status = 'PhaseSevenImplemented'
            Lifecycle = 'Editor'
            NamespacePrefixes = @('AnimarsCatcher.Editor')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @(
                'AnimarsCatcher.Benchmarks.LegacyNavigation',
                'AnimarsCatcher.Gameplay',
                'AnimarsCatcher.Gameplay.Contracts',
                'AnimarsCatcher.Navigation.Editor',
                'AnimarsCatcher.Networking',
                'AnimarsCatcher.Physics.Authoring',
                'AnimarsCatcher.Player',
                'AnimarsCatcher.Presentation'
            )
        }
        @{
            Path = 'Assets/Scripts/Netcode/Editor'
            Assembly = 'AnimarsCatcher.Networking.Editor'
            AsmdefPath = 'Assets/Scripts/Netcode/Editor/AnimarsCatcher.Networking.Editor.asmdef'
            RootNamespace = 'AnimarsCatcher.Networking.Editor'
            Owner = 'Networking Editor'
            Status = 'PhaseSevenImplemented'
            Lifecycle = 'Editor'
            NamespacePrefixes = @('AnimarsCatcher.Networking.Editor')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @('AnimarsCatcher.Networking')
        }
        @{
            Path = 'Assets/Scripts/Netcode'
            Assembly = 'AnimarsCatcher.Networking'
            AsmdefPath = 'Assets/Scripts/Netcode/AnimarsCatcher.Networking.asmdef'
            RootNamespace = 'AnimarsCatcher.Networking'
            Owner = 'Networking'
            Status = 'PhaseSevenTightened'
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
            Assembly = 'AnimarsCatcher.Physics.Authoring'
            AsmdefPath = 'Assets/Scripts/Physics/AnimarsCatcher.Physics.Authoring.asmdef'
            RootNamespace = 'AnimarsCatcher.Physics.Authoring'
            Owner = 'Physics Authoring'
            Status = 'PhaseSevenImplemented'
            Lifecycle = 'Authoring'
            NamespacePrefixes = @('AnimarsCatcher.Physics.Authoring')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @()
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
            AllowedProjectDependencies = @(
                'AnimarsCatcher.Core',
                'AnimarsCatcher.Gameplay'
            )
        }
        @{
            Path = 'Assets/Scripts/Presentation'
            Assembly = 'AnimarsCatcher.Presentation'
            AsmdefPath = 'Assets/Scripts/Presentation/AnimarsCatcher.Presentation.asmdef'
            RootNamespace = 'AnimarsCatcher.Presentation'
            Owner = 'Presentation'
            Status = 'PhaseFiveImplemented'
            Lifecycle = 'Presentation'
            NamespacePrefixes = @('AnimarsCatcher.Presentation')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @(
                'AnimarsCatcher.Gameplay',
                'AnimarsCatcher.Gameplay.Contracts',
                'AnimarsCatcher.Networking',
                'AnimarsCatcher.Player'
            )
        }
    )
}

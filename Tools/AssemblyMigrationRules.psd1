@{
    Version = 9
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
            Path = 'Assets/Scripts/Anis/Navigation/Grid/Editor'
            Assembly = 'AnimarsCatcher.Navigation.Editor'
            AsmdefPath = 'Assets/Scripts/Anis/Navigation/Grid/Editor/AnimarsCatcher.Navigation.Editor.asmdef'
            RootNamespace = 'AnimarsCatcher.Animars.Navigation.Grid.Editor'
            Owner = 'Navigation Editor'
            Status = 'PhaseSevenImplemented'
            Lifecycle = 'Editor'
            NamespacePrefixes = @('AnimarsCatcher.Animars.Navigation.Grid.Editor')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @('AnimarsCatcher.Navigation')
        }
        @{
            Path = 'Assets/Scripts/Anis/Navigation/Grid'
            Assembly = 'AnimarsCatcher.Navigation'
            AsmdefPath = 'Assets/Scripts/Anis/Navigation/Grid/AnimarsCatcher.Navigation.asmdef'
            RootNamespace = 'AnimarsCatcher.Animars.Navigation'
            Owner = 'Navigation'
            Status = 'PhaseSevenTightened'
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
                'AnimarsCatcher.Player'
            )
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
                'AnimarsCatcher.Navigation.Editor',
                'AnimarsCatcher.Networking',
                'AnimarsCatcher.Physics.Authoring',
                'AnimarsCatcher.Player',
                'AnimarsCatcher.Presentation'
            )
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
            AsmrefPath = 'Assets/Scripts/MonoBehaviour/AnimarsCatcher.Presentation.asmref'
            Owner = 'GameObject Presentation'
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
            AllowedProjectDependencies = @('AnimarsCatcher.Gameplay')
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
            Assembly = 'AnimarsCatcher.Physics.Authoring'
            AsmrefPath = 'Assets/Scripts/Terrain/AnimarsCatcher.Physics.Authoring.asmref'
            Owner = 'Terrain Authoring'
            Status = 'PhaseSevenImplemented'
            Lifecycle = 'Authoring'
            NamespacePrefixes = @('AnimarsCatcher.Physics.Authoring')
            RequireNamespace = $true
            EnforceDependencyBoundary = $true
            AllowedProjectDependencies = @()
        }
        @{
            Path = 'Assets/Scripts/UI'
            Assembly = 'AnimarsCatcher.Presentation'
            AsmrefPath = 'Assets/Scripts/UI/AnimarsCatcher.Presentation.asmref'
            Owner = 'ECS Presentation'
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

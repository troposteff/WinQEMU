using System;
using WinQEMU.Core.Models;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// Templates, as in VMware and Hyper-V (VmConfig.IsTemplate): a machine kept to be cloned, never started. The
    /// toolbar offers « Déployer » in place of « Démarrer », which opens the clone dialog on a linked clone.
    /// </summary>
    public partial class MainViewModel
    {
        public bool IsSelectedTemplate => SelectedVm?.IsTemplate == true;

        /// <summary>Démarrer, and « Convertir en modèle », give way to Déployer and the template's banner.</summary>
        public bool IsSelectedNotTemplate => !IsSelectedTemplate;

        /// <summary>Stopped, as for a clone: what turns it into a template is a promise that its disks stay as they are.</summary>
        public bool CanToggleTemplate => SelectedVm is not null && !IsSelectedAlive;

        /// <summary>The clone dialog, for a template: the same conditions as a clone.</summary>
        public bool CanDeploy => IsSelectedTemplate && CanClone;

        /// <summary>Makes the selection a template, or a machine again.</summary>
        public void SetSelectedTemplate(bool isTemplate)
        {
            if (SelectedVm is not { } vm)
                return;

            try
            {
                Manager.SetTemplate(vm, isTemplate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
            {
                ReportError(ex);
                return;
            }
            AppendDiagnostic(isTemplate ? $"{vm.Name}: now a template." : $"{vm.Name}: a virtual machine again.");
            // The library line follows through the descriptor's PropertyChanged (VmListItem.StateText).
            RaiseStateDependentProperties();
        }

        private void RaiseTemplateProperties()
        {
            RaisePropertyChanged(nameof(IsSelectedTemplate));
            RaisePropertyChanged(nameof(IsSelectedNotTemplate));
            RaisePropertyChanged(nameof(CanToggleTemplate));
            RaisePropertyChanged(nameof(CanDeploy));
        }
    }
}

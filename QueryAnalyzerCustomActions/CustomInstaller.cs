using System;
using System.Collections;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace QueryAnalyzerCustomActions
{
    [RunInstaller(true)]
    public partial class CustomInstaller : Installer
    {
        public override void Install(IDictionary stateSaver)
        {
            try
            {
                base.Install(stateSaver);

                // Guardamos el targetdir para usarlo en Commit
                string targetPath = Context.Parameters["targetdir"];
                if (!string.IsNullOrEmpty(targetPath))
                {
                    stateSaver["targetdir"] = targetPath;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error en Custom Action: " + ex.Message);
                throw; // Importante relanzarlo para que el instalador haga rollback
            }
        }

        public override void Commit(IDictionary savedState)
        {
            base.Commit(savedState);

            try
            {
                string targetPath = Context.Parameters["targetdir"];

                if (string.IsNullOrEmpty(targetPath) && savedState.Contains("targetdir"))
                    targetPath = savedState["targetdir"]?.ToString();

                if (string.IsNullOrEmpty(targetPath))
                {
                    MessageBox.Show("No se pudo determinar la carpeta de instalación.");
                    return;
                }

                if (!targetPath.EndsWith("\\"))
                    targetPath += "\\";

                string exePath = Path.Combine(targetPath, "QueryAnalyzer.exe");

                if (File.Exists(exePath))
                {
                    Process.Start(exePath);
                }
                else
                {
                    MessageBox.Show("No se encontró el ejecutable: " + exePath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al ejecutar la aplicación: " + ex.Message);
            }
        }
    }
}
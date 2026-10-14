using System.Runtime.InteropServices;
using Office = Microsoft.Office.Core;

namespace Aperture.Excel
{
    [ComVisible(true)]
    [Guid("490CBA4A-0D76-4265-8EC0-CC892B72F96A")]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class ApertureRibbon : Office.IRibbonExtensibility
    {
        public string GetCustomUI(string ribbonId)
        {
            return @"<customUI xmlns='http://schemas.microsoft.com/office/2009/07/customui'>
              <ribbon><tabs><tab id='Aperture2Tab' label='Aperture2'>
                <group id='Aperture2ChatGroup' label='Помощник Excel'>
                  <button id='Aperture2ShowPane' label='Панель Aperture2' size='large' imageMso='ReviewShowComments' onAction='TogglePane' screentip='Показать или скрыть чат Aperture2' />
                </group>
              </tab></tabs></ribbon>
            </customUI>";
        }

        public void TogglePane(Office.IRibbonControl control) { Globals.ThisAddIn.TogglePane(); }
    }
}

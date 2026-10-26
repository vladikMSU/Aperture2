using System.Runtime.InteropServices;
using Office = Microsoft.Office.Core;

namespace Aperture.Excel
{
    [ComVisible(true)]
    [Guid("490CBA4A-0D76-4265-8EC0-CC892B72F96A")]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class ApertureRibbon : Office.IRibbonExtensibility
    {
        // Its own group at the end of the Home tab, where Claude and ChatGPT for Excel put theirs.
        public string GetCustomUI(string ribbonId)
        {
            return @"<customUI xmlns='http://schemas.microsoft.com/office/2009/07/customui'>
              <ribbon><tabs><tab idMso='TabHome'>
                <group id='Aperture2Group' label='Aperture2'>
                  <button id='Aperture2ShowPane' label='Aperture2' size='large' imageMso='ReviewShowComments' onAction='TogglePane'
                          screentip='Чат Aperture2' supertip='Показать или скрыть чат Aperture2 в окне этой книги.' />
                </group>
              </tab></tabs></ribbon>
            </customUI>";
        }

        public void TogglePane(Office.IRibbonControl control) { Globals.ThisAddIn.TogglePane(); }
    }
}

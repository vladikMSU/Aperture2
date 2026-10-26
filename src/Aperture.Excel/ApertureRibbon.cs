using System.Drawing;
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
        // The group image shows only when the ribbon is too narrow and collapses the group.
        public string GetCustomUI(string ribbonId)
        {
            return @"<customUI xmlns='http://schemas.microsoft.com/office/2009/07/customui' loadImage='LoadImage'>
              <ribbon><tabs><tab idMso='TabHome'>
                <group id='Aperture2Group' label='Aperture2' image='Aperture2'>
                  <button id='Aperture2ShowPane' label='Aperture2' size='large' image='Aperture2' onAction='TogglePane'
                          screentip='Чат Aperture2' supertip='Показать или скрыть чат Aperture2 в окне этой книги.' />
                </group>
              </tab></tabs></ribbon>
            </customUI>";
        }

        public void TogglePane(Office.IRibbonControl control) { Globals.ThisAddIn.TogglePane(); }

        // The aperture that used to sit in the chat header, 32×32 with alpha, one green for the dark
        // and the light ribbon.
        public Bitmap LoadImage(string imageId)
        {
            using (var png = typeof(ApertureRibbon).Assembly.GetManifestResourceStream("Aperture.Excel.Resources.Aperture2.png"))
            using (var image = new Bitmap(png))
                return new Bitmap(image);
        }
    }
}

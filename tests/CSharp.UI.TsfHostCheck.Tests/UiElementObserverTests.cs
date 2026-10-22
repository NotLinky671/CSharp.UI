using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class UiElementObserverTests
{
    [Fact]
    public void BeginUIElementDefaultsToShowAndOnlySuppressesCandidates()
    {
        var candidate = new FakeCandidateElement();
        var unknown = new FakeElement();
        var manager = new FakeUiElementManager(new Dictionary<uint, ITfUIElement>
        {
            [1] = candidate,
            [2] = unknown,
        });
        var traceFields = new List<IDictionary<string, object?>>();
        TsfTrace trace = (stage, kind, fields) =>
        {
            if (kind == "begin" && fields is not null)
            {
                traceFields.Add(fields);
            }
        };
        var observer = new TsfUiElementObserver(manager, trace);

        var show = 0;
        Assert.Equal(TsfHResult.S_OK, observer.BeginUIElement(1, ref show));
        Assert.Equal(1, show); // incoming FALSE must still become TRUE

        show = 1;
        Assert.Equal(TsfHResult.S_OK, observer.BeginUIElement(1, ref show));
        Assert.Equal(1, show);

        show = 1;
        Assert.Equal(TsfHResult.S_OK, observer.BeginUIElement(2, ref show));
        Assert.Equal(1, show); // unknown UI defaults TRUE

        observer.SuppressCandidates = true;
        show = 1;
        Assert.Equal(TsfHResult.S_OK, observer.BeginUIElement(1, ref show));
        Assert.Equal(0, show); // explicit suppression only for real candidate

        show = 1;
        Assert.Equal(TsfHResult.S_OK, observer.BeginUIElement(2, ref show));
        Assert.Equal(1, show); // unknown UI remains TRUE even in suppress mode

        Assert.Contains(traceFields,
            fields => Equals(fields["show_incoming"], 0) && Equals(fields["show_outgoing"], 1));
        Assert.Contains(traceFields,
            fields => Equals(fields["show_incoming"], 1) && Equals(fields["show_outgoing"], 0)
                && Equals(fields["suppressed"], true));
    }

    private sealed class FakeElement : ITfUIElement
    {
        public int GetDescription(out string pbstrDescription)
        {
            pbstrDescription = "unknown-ui";
            return TsfHResult.S_OK;
        }

        public int GetGUID(out Guid pguid)
        {
            pguid = Guid.Empty;
            return TsfHResult.S_OK;
        }

        public int Show(int bShow) => TsfHResult.S_OK;

        public int IsShown(out int pbShow)
        {
            pbShow = 1;
            return TsfHResult.S_OK;
        }
    }

    private sealed class FakeCandidateElement : ITfUIElement, ITfCandidateListUIElement
    {
        public int GetDescription(out string pbstrDescription)
        {
            pbstrDescription = "candidate";
            return TsfHResult.S_OK;
        }

        public int GetGUID(out Guid pguid)
        {
            pguid = Guid.Empty;
            return TsfHResult.S_OK;
        }

        public int Show(int bShow) => TsfHResult.S_OK;

        public int IsShown(out int pbShow)
        {
            pbShow = 1;
            return TsfHResult.S_OK;
        }

        public int GetUpdatedFlags(out uint pdwFlags)
        {
            pdwFlags = 0;
            return TsfHResult.S_OK;
        }

        public int GetDocumentMgr(out ITfDocumentMgr ppdim)
        {
            ppdim = null!;
            return TsfHResult.S_OK;
        }

        public int GetCount(out uint puCount)
        {
            puCount = 1;
            return TsfHResult.S_OK;
        }

        public int GetSelection(out uint puIndex)
        {
            puIndex = 0;
            return TsfHResult.S_OK;
        }

        public int GetString(uint uIndex, out string pstr)
        {
            pstr = "你好";
            return TsfHResult.S_OK;
        }

        public int GetPageIndex(IntPtr pIndex, uint uSize, out uint puPageCnt)
        {
            puPageCnt = 0;
            return TsfHResult.S_OK;
        }

        public int SetPageIndex(IntPtr pIndex, uint uPageCnt) => TsfHResult.S_OK;

        public int GetCurrentPage(out uint puPage)
        {
            puPage = 0;
            return TsfHResult.S_OK;
        }
    }

    private sealed class FakeUiElementManager : ITfUIElementMgr
    {
        private readonly Dictionary<uint, ITfUIElement> _elements;

        internal FakeUiElementManager(Dictionary<uint, ITfUIElement> elements) => _elements = elements;

        public int BeginUIElement(ITfUIElement pElement, ref int pbShow, out uint pdwUIElementId)
        {
            pdwUIElementId = 0;
            return TsfHResult.S_OK;
        }

        public int UpdateUIElement(uint dwUIElementId) => TsfHResult.S_OK;

        public int EndUIElement(uint dwUIElementId) => TsfHResult.S_OK;

        public int GetUIElement(uint dwUIElementId, out ITfUIElement ppElement)
        {
            if (_elements.TryGetValue(dwUIElementId, out var element))
            {
                ppElement = element;
                return TsfHResult.S_OK;
            }

            ppElement = null!;
            return TsfHResult.S_FALSE;
        }

        public int EnumUIElements(out IntPtr ppEnum)
        {
            ppEnum = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }
    }
}

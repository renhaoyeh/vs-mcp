#include "CppUnitTest.h"
using namespace Microsoft::VisualStudio::CppUnitTestFramework;
namespace NativeFixture
{
    TEST_CLASS(Arithmetic)
    {
    public:
        TEST_METHOD(Passes) { Assert::AreEqual(4, 2 + 2); }
        TEST_METHOD(Fails) { Assert::AreEqual(5, 2 + 2, L"Intentional failure for MCP result validation"); }
    };
}

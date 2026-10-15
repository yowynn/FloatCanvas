// Read-only SDK constant/layout diagnostic. No windows, devices, hooks, input or DirectInput8Create calls.
#define WIN32_LEAN_AND_MEAN
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <dinput.h>
#include <stdio.h>
#include <stddef.h>

int main()
{
    printf("{\"Source\":\"Windows SDK c_dfDIMouse2 static constant\",\"DataFormatBytes\":%zu,\"ObjectFormatBytes\":%zu,\"ObjectsPointerOffset\":%zu,\"MouseStateBytes\":%zu,\"CapabilitiesBytes\":%zu,\"Flags\":%lu,\"DataSize\":%lu,\"Count\":%lu,\"Objects\":[",
        sizeof(DIDATAFORMAT), sizeof(DIOBJECTDATAFORMAT), offsetof(DIDATAFORMAT, rgodf), sizeof(DIMOUSESTATE2), sizeof(DIDEVCAPS),
        c_dfDIMouse2.dwFlags, c_dfDIMouse2.dwDataSize, c_dfDIMouse2.dwNumObjs);
    for (DWORD i = 0; i < c_dfDIMouse2.dwNumObjs; ++i)
    {
        const DIOBJECTDATAFORMAT& object = c_dfDIMouse2.rgodf[i];
        if (i) printf(",");
        printf("{\"Offset\":%lu,\"Type\":%lu,\"TypeHex\":\"0x%08lX\",\"Flags\":%lu,\"Guid\":", object.dwOfs, object.dwType, object.dwType, object.dwFlags);
        if (!object.pguid) printf("null");
        else
        {
            const GUID& g = *object.pguid;
            printf("\"%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X\"", g.Data1, g.Data2, g.Data3,
                g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
        }
        printf("}");
    }
    printf("]}\n");
    return 0;
}

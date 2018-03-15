/*****************************************************************************
* RendererDiscoverer.cpp: RendererDiscoverer API
*****************************************************************************
* Copyright � 2018 libvlcpp authors & VideoLAN
*
* Authors: Alexey Sokolov <alexey+vlc@asokolov.org>
*          Hugo Beauz�e-Luyssen <hugo@beauzee.fr>
*
* This program is free software; you can redistribute it and/or modify
* it under the terms of the GNU Lesser General Public License as published by
* the Free Software Foundation; either version 2.1 of the License, or
* (at your option) any later version.
*
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU Lesser General Public License for more details.
*
* You should have received a copy of the GNU Lesser General Public License
* along with this program; if not, write to the Free Software
* Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston MA 02110-1301, USA.
*****************************************************************************/

#include "InstanceCX.hpp"
#include "RendererDiscovererCX.hpp"
#include "EventManagerCX.hpp"

namespace libVLCX
{
    RendererItem::RendererItem(const VLC::RendererDiscoverer::Item& item)
        : m_item(item)
    {
    }

    Platform::String^ RendererItem::name()
    {
        return ToPlatformString(m_item.name());
    }

    Platform::String^ RendererItem::type()
    {
        return ToPlatformString(m_item.type());
    }

    Platform::String^ RendererItem::iconUri()
    {
        return ToPlatformString(m_item.iconUri());
    }

    bool RendererItem::canRenderVideo()
    {
        return m_item.canRenderVideo();
    }

    bool RendererItem::canRenderAudio()
    {
        return m_item.canRenderAudio();
    }

    RendererDiscoverer::RendererDiscoverer(Instance^ inst, Platform::String^ name) :
        m_discoverer(inst->m_instance, FromPlatformString(name))
    {
    }

    bool RendererDiscoverer::start()
    {
        return m_discoverer.start();
    }

    void RendererDiscoverer::stop()
    {
        m_discoverer.stop();
    }

    RendererDiscovererEventManager^ RendererDiscoverer::eventManager()
    {
        if (m_eventManager == nullptr)
            m_eventManager = ref new RendererDiscovererEventManager(m_discoverer.eventManager());
        return m_eventManager;
    }
}